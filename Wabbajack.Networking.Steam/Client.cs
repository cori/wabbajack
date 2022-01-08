using System.Collections.Concurrent;
using System.Security;
using Microsoft.Extensions.Logging;
using SteamKit2;
using SteamKit2.Internal;
using Wabbajack.DTOs.Interventions;
using Wabbajack.Networking.Http.Interfaces;
using Wabbajack.Networking.Steam.UserInterventions;

namespace Wabbajack.Networking.Steam;

public class Client : IDisposable
{
    private readonly ILogger<Client> _logger;
    private readonly HttpClient _httpClient;
    private readonly SteamClient _client;
    private readonly SteamUser _steamUser;
    private readonly CallbackManager _manager;
    private readonly ITokenProvider<SteamLoginState> _token;
    private TaskCompletionSource _loginTask;
    private TaskCompletionSource _connectTask;
    private readonly CancellationTokenSource _cancellationSource;

    private string? _twoFactorCode;
    private string? _authCode;
    private readonly IUserInterventionHandler _interventionHandler;
    private bool _isConnected;
    private bool _isLoggedIn;
    private bool _haveSigFile;

    public TaskCompletionSource _licenseRequest = new();
    private readonly SteamApps _steamApps;

    public SteamApps.LicenseListCallback.License[] Licenses { get; private set; }

    public ConcurrentDictionary<uint, ulong> PackageTokens { get; } = new();

    public ConcurrentDictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo?> PackageInfos { get; } = new();


    public Client(ILogger<Client> logger, HttpClient client, ITokenProvider<SteamLoginState> token,
        IUserInterventionHandler interventionHandler)
    {
        _logger = logger;
        _httpClient = client;
        _interventionHandler = interventionHandler;
        _client = new SteamClient(SteamConfiguration.Create(c =>
        {
            c.WithHttpClientFactory(() => client);
            c.WithProtocolTypes(ProtocolTypes.WebSocket);
            c.WithUniverse(EUniverse.Public);
        }));
        

        _cancellationSource = new CancellationTokenSource();
        
        _token = token;

        _manager = new CallbackManager(_client);

        _steamUser = _client.GetHandler<SteamUser>()!;
        _steamApps = _client.GetHandler<SteamApps>()!;
        
        _manager.Subscribe<SteamClient.ConnectedCallback>( OnConnected );
        _manager.Subscribe<SteamClient.DisconnectedCallback>( OnDisconnected );

        _manager.Subscribe<SteamUser.LoggedOnCallback>( OnLoggedOn );
        _manager.Subscribe<SteamUser.LoggedOffCallback>( OnLoggedOff );

        _manager.Subscribe<SteamApps.LicenseListCallback>(OnLicenseList);
        
        _manager.Subscribe<SteamUser.UpdateMachineAuthCallback>( OnUpdateMachineAuthCallback );

        _isConnected = false;
        _isLoggedIn = false;
        _haveSigFile = false;

        new Thread(() =>
        {
            while (!_cancellationSource.IsCancellationRequested)
            {
                _manager.RunWaitCallbacks(TimeSpan.FromMilliseconds(250));
            }
        })
        {
            Name = "Steam Client callback runner",
            IsBackground = true
        }
        .Start();
    }

    private void OnLicenseList(SteamApps.LicenseListCallback obj)
    {       
        if (obj.Result != EResult.OK)
        {
            _licenseRequest.TrySetException(new SteamException("While getting licenses", obj.Result, EResult.Invalid));
        }
        _logger.LogInformation("Got {LicenseCount} licenses from Steam", obj.LicenseList.Count);
        Licenses = obj.LicenseList.ToArray();
        _licenseRequest.TrySetResult();
    }

    private void OnUpdateMachineAuthCallback(SteamUser.UpdateMachineAuthCallback callback)
    {
        Task.Run(async () =>
        {
            int fileSize;
            byte[] sentryHash;
            
            var token = await _token.Get();

            var ms = new MemoryStream();
            
            if (token?.SentryFile != null)
                await ms.WriteAsync(token.SentryFile);
            
            ms.Seek(callback.Offset, SeekOrigin.Begin);
            ms.Write(callback.Data, 0, callback.BytesToWrite);
            fileSize = (int) ms.Length;

            token!.SentryFile = ms.ToArray();
            sentryHash = CryptoHelper.SHAHash(token.SentryFile);

            await _token.SetToken(token);
            

            _steamUser.SendMachineAuthResponse(new SteamUser.MachineAuthDetails
            {
                JobID = callback.JobID,
                FileName = callback.FileName,

                BytesWritten = callback.BytesToWrite,
                FileSize = token.SentryFile.Length,
                Offset = callback.Offset,
                Result = EResult.OK,
                LastError = 0,
                OneTimePassword = callback.OneTimePassword,
                SentryFileHash = sentryHash
            });

            _haveSigFile = true;
            _loginTask.TrySetResult();
        });
    }

    private void OnLoggedOff(SteamUser.LoggedOffCallback obj)
    {
        _isLoggedIn = false;
    }

    private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
    {
        Task.Run(async () =>
        {
            var isSteamGuard = callback.Result == EResult.AccountLogonDenied;
            var is2FA = callback.Result == EResult.AccountLoginDeniedNeedTwoFactor;

            if (isSteamGuard || is2FA)
            {
                _logger.LogInformation("Account is SteamGuard protected");
                if (is2FA)
                {
                    var intervention = new GetAuthCode(GetAuthCode.AuthType.TwoFactorAuth);
                    _interventionHandler.Raise(intervention);
                    _twoFactorCode = await intervention.Task;
                }
                else
                {
                    var intervention = new GetAuthCode(GetAuthCode.AuthType.EmailCode);
                    _interventionHandler.Raise(intervention);
                    _authCode = await intervention.Task;
                }
                
                var tcs = Login(_loginTask);
                return;
            }

            if (callback.Result != EResult.OK)
            {
                _loginTask.SetException(new SteamException("Unable to log in", callback.Result, callback.ExtendedResult));
                return;
            }

            _isLoggedIn = true;
            _logger.LogInformation("Logged into Steam");
            if (_haveSigFile) 
                _loginTask.SetResult();
        });
    }

    private void OnDisconnected(SteamClient.DisconnectedCallback obj)
    {
        _isConnected = false;
        _logger.LogInformation("Logged out");
    }

    private void OnConnected(SteamClient.ConnectedCallback obj)
    {
        Task.Run(async () =>
        {
            var state = (await _token.Get())!;
            _logger.LogInformation("Connected to Steam, logging in as {User}", state.User);

            byte[]? sentryHash = null;
            
            
            if (state.SentryFile != null)
            {
                _logger.LogInformation("Existing login keys found, reusing");
                sentryHash = CryptoHelper.SHAHash(state.SentryFile);
                _haveSigFile = true;
            }
            else
            {
                _haveSigFile = false;
            }
            

            _isConnected = true;
            
            _steamUser.LogOn(new SteamUser.LogOnDetails
            {
                Username = state.User,
                Password = state.Password,
                AuthCode = _authCode,
                TwoFactorCode = _twoFactorCode,
                SentryFileHash = sentryHash,
                RequestSteam2Ticket = true
            });
        });
    }
    
    public Task Connect()
    {
        _connectTask = new TaskCompletionSource();

        _client.Connect();
        return _connectTask.Task;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _cancellationSource.Cancel();
        _cancellationSource.Dispose();
    }

    public async Task Login(TaskCompletionSource? tcs = null)
    {
        _loginTask = tcs ?? new TaskCompletionSource();
        _logger.LogInformation("Attempting login");
        _client.Connect();

        await _loginTask.Task; 
        await _licenseRequest.Task;
    }

    public async Task<Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo?>> GetPackageInfos(IEnumerable<uint> packageIds)
    {
        var packages = packageIds.Where(id => !PackageInfos.ContainsKey(id)).ToList();

        if (packages.Count > 0)
        {
            var packageRequests = new List<SteamApps.PICSRequest>();

            foreach (var package in packages)
            {
                var request = new SteamApps.PICSRequest(package);
                if (PackageTokens.TryGetValue(package, out var token))
                {
                    request.AccessToken = token;
                }

                packageRequests.Add(request);
            }

            _logger.LogInformation("Requesting {Count} package infos", packageRequests.Count);

            var results = await _steamApps.PICSGetProductInfo(new List<SteamApps.PICSRequest>(), packageRequests);

            if (results.Failed)
                throw new SteamException("Exception getting product info", EResult.Invalid, EResult.Invalid);

            foreach (var packageInfo in results.Results!)
            {
                foreach (var package in packageInfo.Packages.Select(v => v.Value))
                {
                    PackageInfos[package.ID] = package;
                }

                foreach (var package in packageInfo.UnknownPackages)
                {
                    PackageInfos[package] = null;
                }
            }
        }

        return packages.Distinct().ToDictionary(p => p, p => PackageInfos[p]);

    }
}