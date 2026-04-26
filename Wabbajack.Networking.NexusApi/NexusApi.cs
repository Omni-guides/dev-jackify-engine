using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wabbajack.Common;
using Wabbajack.DTOs;
using Wabbajack.DTOs.Logins;
using Wabbajack.DTOs.OAuth;
using Wabbajack.Networking.Http;
using Wabbajack.Networking.Http.Interfaces;
using Wabbajack.Networking.NexusApi.DTOs;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;
using Wabbajack.RateLimiter;
using Wabbajack.Server.DTOs;

namespace Wabbajack.Networking.NexusApi;

public class NexusApi
{
    private readonly ApplicationInfo _appInfo;
    private readonly HttpClient _client;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly IResource<HttpClient> _limiter;
    private readonly ILogger<NexusApi> _logger;
    public readonly ITokenProvider<NexusOAuthState> AuthInfo;
    private DateTime _lastValidated;
    private (ValidateInfo info, ResponseMetadata header) _lastValidatedInfo; 
    private readonly AsyncLock _authLock = new();
    private readonly AsyncLock _authValidationLock = new();
    // In-memory OAuth state for NEXUS_OAUTH_INFO environment variable tokens (cannot be persisted back to env var)
    private NexusOAuthState? _envVarOAuthState;

    public NexusApi(ITokenProvider<NexusOAuthState> authInfo, ILogger<NexusApi> logger, HttpClient client,
        IResource<HttpClient> limiter, ApplicationInfo appInfo, JsonSerializerOptions jsonOptions)
    {
        AuthInfo = authInfo;
        _logger = logger;
        _client = client;
        _appInfo = appInfo;
        _jsonOptions = jsonOptions;
        _limiter = limiter;
        _lastValidated = DateTime.MinValue;
        _lastValidatedInfo = default;
    }

    public virtual async Task<(ValidateInfo info, ResponseMetadata header)> Validate(
        CancellationToken token = default)
    {
        var (isApi, code) = await GetAuthInfo();

        using var _ = await _authValidationLock.WaitAsync();

        if (isApi)
        {
            var msg = await GenerateMessage(HttpMethod.Get, Endpoints.Validate);
            _lastValidatedInfo = await Send<ValidateInfo>(msg, token);
        }
        else if(_lastValidated < DateTime.Now - TimeSpan.FromMinutes(5)) // We don't want to spam the validate endpoint when starting a modlist download
        {
            try
            {
                var msg = await GenerateMessage(HttpMethod.Get, Endpoints.OAuthValidate);
                var (data, header) = await Send<OAuthUserInfo>(msg, token);
                var roles = data.MembershipRoles ?? Array.Empty<string>();
                var isPremium = roles.Contains("premium");
                _logger.LogDebug("Nexus userinfo: user={Name} membership_roles=[{Roles}] IsPremium={IsPremium}",
                    data.Name, string.Join(", ", roles), isPremium);
                if (!isPremium && roles.Length > 0)
                    _logger.LogInformation("Nexus account has roles [{Roles}] but not 'premium' — account may not have active Premium", string.Join(", ", roles));
                var validateInfo = new ValidateInfo
                {
                    IsPremium = isPremium,
                    Name = data.Name,
                };
                _lastValidatedInfo = (validateInfo, header);
                _lastValidated = DateTime.Now;
            }
            catch (HttpException ex) when (ex.Code == (int)HttpStatusCode.Unauthorized || ex.Code == (int)HttpStatusCode.Forbidden)
            {
                // Cache the failure to prevent tight retry loops when token is invalid/expired
                _lastValidatedInfo = (new ValidateInfo { IsPremium = false, Name = "" }, new ResponseMetadata());
                _lastValidated = DateTime.Now - TimeSpan.FromMinutes(4); // expires in 1 min
                throw;
            }
        }

        return _lastValidatedInfo;
    }
    
    public async Task<(ValidateInfo info, ResponseMetadata header)> ValidateCached(
        CancellationToken token = default)
    {
        if (DateTime.Now - _lastValidated < TimeSpan.FromMinutes(10))
        {
            return _lastValidatedInfo;
        }

        await Validate(token);

        return _lastValidatedInfo;
    }

    public virtual async Task<(ModInfo info, ResponseMetadata header)> ModInfo(string nexusGameName, long modId,
        CancellationToken token = default)
    {
        var msg = await GenerateMessage(HttpMethod.Get, Endpoints.ModInfo, nexusGameName, modId);
        return await Send<ModInfo>(msg, token);
    }

    public virtual async Task<(ModFiles info, ResponseMetadata header)> ModFiles(string nexusGameName, long modId,
        CancellationToken token = default)
    {
        var msg = await GenerateMessage(HttpMethod.Get, Endpoints.ModFiles, nexusGameName, modId);
        return await Send<ModFiles>(msg, token);
    }

    public virtual async Task<(ModFile info, ResponseMetadata header)> FileInfo(string nexusGameName, long modId,
        long fileId, CancellationToken token = default)
    {
        var msg = await GenerateMessage(HttpMethod.Get, Endpoints.ModFile, nexusGameName, modId, fileId);
        return await Send<ModFile>(msg, token);
    }

    public virtual async Task<(DownloadLink[] info, ResponseMetadata header)> DownloadLink(string nexusGameName,
        long modId, long fileId, CancellationToken token = default)
    {
        var msg = await GenerateMessage(HttpMethod.Get, Endpoints.DownloadLink, nexusGameName, modId, fileId);
        return await Send<DownloadLink[]>(msg, token);
    }

    protected virtual async Task<(T data, ResponseMetadata header)> Send<T>(HttpRequestMessage msg,
        CancellationToken token = default)
    {
        using var job = await _limiter.Begin($"API call to the Nexus {msg.RequestUri!.PathAndQuery}", 0, token);

        _logger.LogDebug("Nexus API Request: {Method} {Uri}", msg.Method, msg.RequestUri);
        
        using var result = await _client.SendAsync(msg, token);
        
        _logger.LogDebug("Nexus API Response: {StatusCode} {ReasonPhrase}", result.StatusCode, result.ReasonPhrase);
        
        if (!result.IsSuccessStatusCode)
        {
            // Enhanced logging for authentication failures
            if (result.StatusCode == HttpStatusCode.Unauthorized || result.StatusCode == HttpStatusCode.Forbidden)
            {
                // Check token source without calling GetAuthInfo (to avoid recursion)
                var hasStoredToken = AuthInfo.HaveToken();
                var hasEnvToken = Environment.GetEnvironmentVariable("NEXUS_API_KEY") != null;
                var tokenSource = hasStoredToken ? "encrypted file (can refresh)" : 
                    (hasEnvToken ? "NEXUS_API_KEY environment variable (cannot refresh)" : "none");
                
                var errorBody = await result.Content.ReadAsStringAsync(token);
                _logger.LogError("Nexus API authentication failed: {StatusCode} {ReasonPhrase}. " +
                    "Token source: {TokenSource}. " +
                    "Response body: {ErrorBody}. " +
                    "If using OAuth token from environment variable, the token may have expired. " +
                    "Environment variable tokens cannot be refreshed automatically. " +
                    "For long-running installations, Jackify should refresh tokens before passing them, " +
                    "or store tokens in encrypted file for automatic refresh capability.",
                    result.StatusCode, result.ReasonPhrase, tokenSource, errorBody);
            }
            else
            {
                var errorBody = await result.Content.ReadAsStringAsync(token);
                _logger.LogError("Nexus API call failed: {StatusCode} {ReasonPhrase}, Response body: {ErrorBody}",
                    result.StatusCode, result.ReasonPhrase, errorBody);
            }
            throw new HttpException(result);
        }

        var headers = ParseHeaders(result);
        job.Size = result.Content.Headers.ContentLength ?? 0;
        await job.Report((int) (result.Content.Headers.ContentLength ?? 0), token);

        var body = await result.Content.ReadAsByteArrayAsync(token);
        
        return (JsonSerializer.Deserialize<T>(body, _jsonOptions)!, headers);
    }

    protected virtual ResponseMetadata ParseHeaders(HttpResponseMessage result)
    {
        var metaData = new ResponseMetadata();

        {
            if (result.Headers.TryGetValues("x-rl-daily-limit", out var limits))
                if (int.TryParse(limits.First(), out var limit))
                    metaData.DailyLimit = limit;
        }

        {
            if (result.Headers.TryGetValues("x-rl-daily-remaining", out var limits))
                if (int.TryParse(limits.First(), out var limit))
                    metaData.DailyRemaining = limit;
        }

        {
            if (result.Headers.TryGetValues("x-rl-daily-reset", out var resets))
                if (DateTime.TryParse(resets.First(), out var reset))
                    metaData.DailyReset = reset;
        }

        {
            if (result.Headers.TryGetValues("x-rl-hourly-limit", out var limits))
                if (int.TryParse(limits.First(), out var limit))
                    metaData.HourlyLimit = limit;
        }

        {
            if (result.Headers.TryGetValues("x-rl-hourly-remaining", out var limits))
                if (int.TryParse(limits.First(), out var limit))
                    metaData.HourlyRemaining = limit;
        }

        {
            if (result.Headers.TryGetValues("x-rl-hourly-reset", out var resets))
                if (DateTime.TryParse(resets.First(), out var reset))
                    metaData.HourlyReset = reset;
        }


        {
            if (result.Headers.TryGetValues("x-runtime", out var runtimes))
                if (double.TryParse(runtimes.First(), out var reset))
                    metaData.Runtime = reset;
        }

        _logger.LogDebug("Nexus API call finished: {Runtime} - Remaining Limit: {RemainingLimit}",
            metaData.Runtime, Math.Max(metaData.DailyRemaining, metaData.HourlyRemaining));

        return metaData;
    }

    protected virtual async ValueTask<HttpRequestMessage> GenerateMessage(HttpMethod method, string uri,
        params object?[] parameters)
    {
        var msg = new HttpRequestMessage();
        msg.Method = method;

        var userAgent =
            $"{_appInfo.ApplicationSlug}/{_appInfo.Version} ({_appInfo.OSVersion}; {_appInfo.Platform})";

        await AddAuthHeaders(msg);
        
        if (uri.StartsWith("http"))
        {
            msg.RequestUri = new Uri($"{string.Format(uri, parameters)}");
        }
        else
        {
            msg.RequestUri = new Uri($"https://api.nexusmods.com/{string.Format(uri, parameters)}");
        }

        msg.Headers.Add("User-Agent", userAgent);
        msg.Headers.Add("Application-Name", _appInfo.ApplicationSlug);
        msg.Headers.Add("Application-Version", _appInfo.Version);
        

        msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return msg;
    }
    
    private async ValueTask AddAuthHeaders(HttpRequestMessage msg)
    {
        var (isApi, code) = await GetAuthInfo();
        if (string.IsNullOrWhiteSpace(code))
            throw new Exception("No API Key or OAuth Token found for NexusMods");
        
        if (isApi)
            msg.Headers.Add("apikey", code);
        else
        {
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", code);
        }

    }

    private async ValueTask<(bool IsApiKey, string code)> GetAuthInfo()
    {
        using var _ = await _authLock.WaitAsync();
        
        // Priority: encrypted file > NEXUS_OAUTH_INFO env var > NEXUS_API_KEY env var
        // Within each source, it's EITHER OAuth OR API key (user's explicit choice), not both
        // NOTE: GUI writes encrypted files (~/.config/jackify/nexus-oauth.json) with AES-GCM
        // Engine does NOT support AES-GCM - GUI should pass OAuth via NEXUS_OAUTH_INFO env var
        var encryptedFilePath = JackifyConfig.GetDataDirectory().Combine("encrypted", "nexus-oauth-info");
        var hasTokenFile = encryptedFilePath.FileExists();
        
        if (hasTokenFile)
        {
            var info = await AuthInfo.Get();
            
            // Encrypted file: EITHER OAuth OR API key (user's choice)
            if (info!.OAuth != null)
            {
                // User chose OAuth - use it with refresh capability
                // Check expiry with 15-minute buffer (user requirement)
                if (IsTokenExpiringSoon(info.OAuth, TimeSpan.FromMinutes(15)))
                {
                    try
                    {
                        var expiresAt = DateTime.FromFileTimeUtc(info.OAuth.ReceivedAt) + TimeSpan.FromSeconds(info.OAuth.ExpiresIn);
                        var timeUntilExpiry = expiresAt - DateTimeOffset.UtcNow;
                        var minutesUntilExpiry = (int)timeUntilExpiry.TotalMinutes;
                        _logger.LogDebug("OAuth token expiring in {Minutes} minutes, triggering refresh...", minutesUntilExpiry);
                        info = await RefreshToken(info, CancellationToken.None);
                    }
                    catch (Exception refreshEx)
                    {
                        // Refresh of the stored encrypted token failed (e.g. token revoked after user re-auth in GUI).
                        // Delete the stale file and fall through to check NEXUS_OAUTH_INFO env var, which contains
                        // the fresh token the GUI just passed in. Without this, a stale encrypted file permanently
                        // blocks a freshly-authenticated session.
                        _logger.LogWarning("Stored OAuth token refresh failed ({ErrorMessage}); deleting stale token file and falling back to env var.", refreshEx.Message);
                        try { await AuthInfo.Delete(); } catch { /* best-effort delete */ }
                        info = new NexusOAuthState(); // clear OAuth so we fall through below
                    }
                }
                
                if (info.OAuth?.AccessToken != null)
                {
                    _logger.LogDebug("Using OAuth token from encrypted file");
                    // Write token status to file for GUI to read (fire and forget)
                    Task.Run(async () => await WriteTokenStatusFile(info)).ConfigureAwait(false);
                    return (false, info.OAuth.AccessToken);
                }
            }
            else if (!string.IsNullOrWhiteSpace(info.ApiKey))
            {
                // User chose API key - use it (no refresh capability)
                _logger.LogDebug("Using API key from encrypted file");
                // Write token status to file for GUI to read (fire and forget)
                Task.Run(async () => await WriteTokenStatusFile(info)).ConfigureAwait(false);
                return (true, info.ApiKey);
            }
            // If encrypted file exists but has no valid auth, fall through to check env vars
        }
        
        // Check environment variables: EITHER NEXUS_OAUTH_INFO OR NEXUS_API_KEY (user's choice)
        // Check NEXUS_OAUTH_INFO first (OAuth with refresh)
        if (Environment.GetEnvironmentVariable("NEXUS_OAUTH_INFO") is { } oauthInfoJson)
        {
            try
            {
                // Use in-memory state if available, otherwise parse directly from env var
                // IMPORTANT: Don't use AuthInfo.Get() here - it would read from encrypted file if it exists
                // We need to deserialize directly from the NEXUS_OAUTH_INFO environment variable
                if (_envVarOAuthState == null)
                {
                    var info = JsonSerializer.Deserialize<NexusOAuthState>(oauthInfoJson, _jsonOptions);
                    if (info?.OAuth != null)
                    {
                        // Ensure ReceivedAt is set if missing (calculate from current time if not present)
                        if (info.OAuth.ReceivedAt == 0)
                        {
                            info.OAuth.ReceivedAt = DateTime.UtcNow.ToFileTimeUtc();
                            _logger.LogDebug("Set ReceivedAt for OAuth token from NEXUS_OAUTH_INFO environment variable");
                        }
                        _envVarOAuthState = info;
                    }
                    else
                    {
                        _logger.LogError("NEXUS_OAUTH_INFO environment variable does not contain valid OAuth state.");
                        throw new Exception("NEXUS_OAUTH_INFO environment variable does not contain valid OAuth state");
                    }
                }
                
                // User chose OAuth - use it with refresh capability
                if (_envVarOAuthState.OAuth != null)
                {
                    // Check expiry with 15-minute buffer (user requirement)
                    if (IsTokenExpiringSoon(_envVarOAuthState.OAuth, TimeSpan.FromMinutes(15)))
                    {
                        try
                        {
                        var expiresAt = DateTime.FromFileTimeUtc(_envVarOAuthState.OAuth.ReceivedAt) + TimeSpan.FromSeconds(_envVarOAuthState.OAuth.ExpiresIn);
                        var timeUntilExpiry = expiresAt - DateTimeOffset.UtcNow;
                        var minutesUntilExpiry = (int)timeUntilExpiry.TotalMinutes;
                        _logger.LogDebug("OAuth token expiring in {Minutes} minutes, triggering refresh...", minutesUntilExpiry);
                            _envVarOAuthState = await RefreshTokenForEnvVar(_envVarOAuthState, CancellationToken.None);
                        }
                        catch (Exception refreshEx)
                        {
                            _logger.LogError(refreshEx, "OAuth token refresh failed: {ErrorMessage}. OAuth authentication will fail.", refreshEx.Message);
                            throw; // Don't fall back - user chose OAuth, so respect that choice
                        }
                    }
                    
                    if (_envVarOAuthState.OAuth?.AccessToken != null)
                    {
                        _logger.LogDebug("Using OAuth token from NEXUS_OAUTH_INFO env var");
                        // Write token status to file for GUI to read (fire and forget)
                        Task.Run(async () => await WriteTokenStatusFile(_envVarOAuthState)).ConfigureAwait(false);
                        return (false, _envVarOAuthState.OAuth.AccessToken);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to use OAuth token from NEXUS_OAUTH_INFO environment variable.");
                throw; // Don't fall back - user chose OAuth, so respect that choice
            }
        }
        // Check NEXUS_API_KEY (API key or OAuth token without refresh)
        else if (Environment.GetEnvironmentVariable("NEXUS_API_KEY") is { } apiKey)
        {
            // Detect OAuth token: JWT tokens start with "eyJ" (Base64 encoded JSON header)
            // If it's an OAuth token, use Bearer authentication; otherwise use API key
            var isOAuthToken = apiKey.StartsWith("eyJ", StringComparison.Ordinal);
            
            if (isOAuthToken)
            {
                _logger.LogDebug("Detected OAuth token from NEXUS_API_KEY environment variable (length: {Length}, starts with: {Prefix}). " +
                    "Note: This token cannot be auto-refreshed. For long-running installs, use NEXUS_OAUTH_INFO instead.",
                    apiKey.Length, apiKey.Substring(0, Math.Min(20, apiKey.Length)));
            }
            else
            {
                _logger.LogDebug("Detected API key from NEXUS_API_KEY environment variable (length: {Length})",
                    apiKey.Length);
            }
            
            // Write token status to file for GUI to read (fire and forget)
            Task.Run(async () => await WriteTokenStatusFile(null)).ConfigureAwait(false);
            return (IsApiKey: !isOAuthToken, apiKey);
        }

        return default;
    }
    
    /// <summary>
    /// Check if token is expiring soon (within the specified buffer time)
    /// </summary>
    private bool IsTokenExpiringSoon(Wabbajack.DTOs.OAuth.JwtTokenReply oauth, TimeSpan buffer)
    {
        if (oauth.ReceivedAt == 0)
            return true; // If ReceivedAt is not set, consider it expired
        
        var expiresAt = DateTime.FromFileTimeUtc(oauth.ReceivedAt) + TimeSpan.FromSeconds(oauth.ExpiresIn);
        var expiresAtWithBuffer = expiresAt - buffer;
        return DateTimeOffset.UtcNow >= expiresAtWithBuffer;
    }
    
    private async Task<NexusOAuthState> RefreshToken(NexusOAuthState state, CancellationToken cancel)
    {
        // Read client_id from environment variable, default to "jackify" (Jackify Engine's registered client_id)
        var clientId = Environment.GetEnvironmentVariable("NEXUS_OAUTH_CLIENT_ID") ?? "jackify";
        _logger.LogInformation("Refreshing OAuth Token using client_id: {ClientId}", clientId);
        var request = new Dictionary<string, string>
        {
            { "grant_type", "refresh_token" },
            { "client_id", clientId },
            { "refresh_token", state.OAuth!.RefreshToken },
        };

        var content = new FormUrlEncodedContent(request);

        var response = await _client.PostAsync($"https://users.nexusmods.com/oauth/token", content, cancel);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancel);
            _logger.LogError("Nexus OAuth Token refresh failed: Status {StatusCode}, Reason: {ResponseReasonPhrase}, Body: {ErrorBody}",
                response.StatusCode, response.ReasonPhrase, errorBody);
            // Don't continue with null token - this would break authentication
            throw new HttpException(response);
        }
        
        var responseString = await response.Content.ReadAsStringAsync(cancel);
        var newJwt = JsonSerializer.Deserialize<JwtTokenReply>(responseString);
        if (newJwt != null) 
        {
            newJwt.ReceivedAt = DateTime.UtcNow.ToFileTimeUtc();
            var expiresInHours = newJwt.ExpiresIn / 3600.0;
            var expiresInMinutes = newJwt.ExpiresIn / 60.0;
            if (expiresInHours >= 1.0)
            {
                _logger.LogDebug("OAuth token refreshed successfully, expires in {Hours:F1} hours", expiresInHours);
            }
            else
            {
                _logger.LogDebug("OAuth token refreshed successfully, expires in {Minutes:F0} minutes", expiresInMinutes);
            }
        }
        else
        {
            _logger.LogError("OAuth token refresh failed: refresh returned null JWT. Response body: {ResponseBody}", responseString);
            throw new Exception("OAuth token refresh returned null JWT");
        }
        
        state.OAuth = newJwt;
        await AuthInfo.SetToken(state);
        // Write token status to file so GUI can read it during installs
        await WriteTokenStatusFile(state);
        return state;
    }
    
    /// <summary>
    /// Refresh token for environment variable OAuth state (keeps refreshed token in memory, cannot persist to env var)
    /// </summary>
    private async Task<NexusOAuthState> RefreshTokenForEnvVar(NexusOAuthState state, CancellationToken cancel)
    {
        // Read client_id from environment variable, default to "jackify" (Jackify Engine's registered client_id)
        var clientId = Environment.GetEnvironmentVariable("NEXUS_OAUTH_CLIENT_ID") ?? "jackify";
        _logger.LogInformation("Refreshing OAuth Token from NEXUS_OAUTH_INFO environment variable using client_id: {ClientId}", clientId);
        var request = new Dictionary<string, string>
        {
            { "grant_type", "refresh_token" },
            { "client_id", clientId },
            { "refresh_token", state.OAuth!.RefreshToken },
        };

        var content = new FormUrlEncodedContent(request);

        var response = await _client.PostAsync($"https://users.nexusmods.com/oauth/token", content, cancel);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancel);
            _logger.LogError("Nexus OAuth Token refresh failed: Status {StatusCode}, Reason: {ResponseReasonPhrase}, Body: {ErrorBody}",
                response.StatusCode, response.ReasonPhrase, errorBody);
            throw new HttpException(response);
        }
        
        var responseString = await response.Content.ReadAsStringAsync(cancel);
        var newJwt = JsonSerializer.Deserialize<JwtTokenReply>(responseString);
        if (newJwt != null) 
        {
            newJwt.ReceivedAt = DateTime.UtcNow.ToFileTimeUtc();
            var expiresInHours = newJwt.ExpiresIn / 3600.0;
            var expiresInMinutes = newJwt.ExpiresIn / 60.0;
            if (expiresInHours >= 1.0)
            {
                _logger.LogDebug("OAuth token refreshed successfully, expires in {Hours:F1} hours", expiresInHours);
            }
            else
            {
                _logger.LogDebug("OAuth token refreshed successfully, expires in {Minutes:F0} minutes", expiresInMinutes);
            }
        }
        else
        {
            _logger.LogError("OAuth token refresh failed: refresh returned null JWT. Response body: {ResponseBody}", responseString);
            throw new Exception("OAuth token refresh returned null JWT");
        }
        
        state.OAuth = newJwt;
        // Don't call AuthInfo.SetToken() - that would try to save to encrypted file
        // Instead, keep the refreshed state in memory
        // Write token status to file so GUI can read it during installs
        await WriteTokenStatusFile(state);
        // Write updated token state to writeback file so Jackify can persist the new refresh token
        await WriteTokenWriteback(state);
        return state;
    }

    /// <summary>
    /// If JACKIFY_TOKEN_WRITEBACK is set, writes the current NexusOAuthState as JSON to that path.
    /// Jackify sets this env var before launching the engine and reads it after the engine exits,
    /// allowing the new refresh token from rotation to be persisted back to Jackify's token store.
    /// </summary>
    private async Task WriteTokenWriteback(NexusOAuthState state)
    {
        var writebackPath = Environment.GetEnvironmentVariable("JACKIFY_TOKEN_WRITEBACK");
        if (string.IsNullOrWhiteSpace(writebackPath))
            return;

        try
        {
            var json = JsonSerializer.Serialize(state, _jsonOptions);
            await File.WriteAllTextAsync(writebackPath, json);
            _logger.LogDebug("OAuth token writeback written to {Path}", writebackPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write OAuth token writeback to {Path}", writebackPath);
        }
    }

    public async Task<(UpdateEntry[], ResponseMetadata headers)> GetUpdates(Game game, CancellationToken token)
    {
        var msg = await GenerateMessage(HttpMethod.Get, Endpoints.Updates, game.MetaData().NexusName, "1m");
        return await Send<UpdateEntry[]>(msg, token);
    }

    public async Task<ChunkStatus> ChunkStatus(UploadDefinition definition, Chunk chunk)
    {
        var msg = new HttpRequestMessage();
        msg.Method = HttpMethod.Get;

        var query =
            $"resumableChunkNumber={chunk.Index + 1}&resumableCurrentChunkSize={chunk.Size}&resumableTotalSize={definition.FileSize}"
            + $"&resumableType=&resumableIdentifier={definition.ResumableIdentifier}&resumableFilename={definition.ResumableRelativePath}"
            + $"&resumableRelativePath={definition.ResumableRelativePath}&resumableTotalChunks={definition.Chunks().Count()}";

        msg.RequestUri = new Uri($"https://upload.nexusmods.com/uploads/chunk?{query}");

        using var result = await _client.SendAsync(msg);
        if (!result.IsSuccessStatusCode)
            throw new HttpException(result);
        if (result.StatusCode == HttpStatusCode.NoContent)
            return DTOs.ChunkStatus.NoContent;

        var status = await result.Content.ReadFromJsonAsync<ChunkStatusResult>();
        return status?.Status ?? false ? DTOs.ChunkStatus.Done : DTOs.ChunkStatus.Waiting;
    }

    public async Task<ChunkStatusResult> UploadChunk(UploadDefinition d, Chunk chunk)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent((chunk.Index+1).ToString()), "resumableChunkNumber");
        form.Add(new StringContent(UploadDefinition.ChunkSize.ToString()), "resumableChunkSize");
        form.Add(new StringContent(chunk.Size.ToString()), "resumableCurrentChunkSize");
        form.Add(new StringContent(d.FileSize.ToString()), "resumableTotalSize");
        form.Add(new StringContent(""), "resumableType");
        form.Add(new StringContent(d.ResumableIdentifier), "resumableIdentifier");
        form.Add(new StringContent(d.ResumableRelativePath), "resumableFilename");
        form.Add(new StringContent(d.ResumableRelativePath), "resumableRelativePath");
        form.Add(new StringContent(d.Chunks().Count().ToString()), "resumableTotalChunks");

        await using var ms = new MemoryStream();
        await using var fs = d.Path.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Position = chunk.Offset;
        await fs.CopyToLimitAsync(ms, (int)chunk.Size, CancellationToken.None);
        ms.Position = 0;
        
        form.Add(new StreamContent(ms), "file", "blob");

        var msg = new HttpRequestMessage(HttpMethod.Post,  "https://upload.nexusmods.com/uploads/chunk");
        msg.Content = form;

        var result = await _client.SendAsync(msg);
        if (result.StatusCode != HttpStatusCode.OK)
            throw new HttpException(result);

        var response = await result.Content.ReadFromJsonAsync<ChunkStatusResult>(_jsonOptions);
        return response!;
    }
    public async Task UploadFile(UploadDefinition d)
    {
        _logger.LogInformation("Checking Access");
        await CheckAccess();
        
        _logger.LogInformation("Checking chunk status");
        
        var numberOfChunks = d.Chunks().Count();
        var chunkStatus = new ChunkStatusResult();
        foreach (var chunk in d.Chunks())
        {
            var status = await ChunkStatus(d, chunk);
            _logger.LogInformation("({Index}/{MaxChunks}) Chunk status: {Status}", chunk.Index, numberOfChunks, status);
            if (status == DTOs.ChunkStatus.NoContent)
            {
                _logger.LogInformation("({Index}/{MaxChunks}) Uploading", chunk.Index, numberOfChunks);
                chunkStatus = await UploadChunk(d, chunk);
            }
        }

        await WaitForFileStatus(chunkStatus);

        await AddFile(d, chunkStatus);

    }

    private async Task CheckAccess()
    {
        var msg = new HttpRequestMessage(HttpMethod.Get, "https://www.nexusmods.com/users/myaccount");
        throw new NotSupportedException("Uploading to NexusMods is currently disabled");
        using var response = await _client.SendAsync(msg);
        var body = await response.Content.ReadAsStringAsync();

        if (body.Contains("You are not allowed to access this area!"))
            throw new HttpException(403, "Nexus Cookies are incorrect");
    }

    private async Task AddFile(UploadDefinition d, ChunkStatusResult status)
    {
        _logger.LogInformation("Saving file update {Name} to {Game}:{ModId}", d.Path.FileName, d.Game, d.ModId);
        
        var msg = new HttpRequestMessage(HttpMethod.Post,
            "https://www.nexusmods.com/Core/Libs/Common/Managers/Mods?AddFile");
        msg.Headers.Referrer =
            new Uri(
                $"https://www.nexusmods.com/{d.Game.MetaData().NexusName}/mods/edit/?id={d.ModId}&game_id={d.GameId}&step=files");
        
        throw new NotSupportedException("Uploading to NexusMods is currently disabled");
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(d.GameId.ToString()), "game_id");
        form.Add(new StringContent(d.Name), "name");
        form.Add(new StringContent(d.Version), "file-version");
        form.Add(new StringContent((d.RemoveOldVersion ? 1 : 0).ToString()), "update-version");
        form.Add(new StringContent(((int)Enum.Parse<Category>(d.Category, true)).ToString()), "category");
        form.Add(new StringContent((d.NewExisting ? 1 : 0).ToString()), "new-existing");
        form.Add(new StringContent(d.OldFileId.ToString()), "old_file_id");
        form.Add(new StringContent((d.RemoveOldVersion ? 1 : 0).ToString()), "remove-old-version");
        form.Add(new StringContent(d.BriefOverview), "brief-overview");
        form.Add(new StringContent((d.SetAsMain ? 1 : 0).ToString()), "set_as_main_nmm");
        form.Add(new StringContent(status.UUID), "file_uuid");
        form.Add(new StringContent(d.FileSize.ToString()), "file_size");
        form.Add(new StringContent(d.ModId.ToString()), "mod_id");
        form.Add(new StringContent(d.ModId.ToString()), "id");
        form.Add(new StringContent("save"), "action");
        form.Add(new StringContent(status.Filename), "uploaded_file");
        form.Add(new StringContent(d.Path.FileName.ToString()), "original_file");
        msg.Content = form;
        
        using var result = await _client.SendAsync(msg);
        if (!result.IsSuccessStatusCode)
            throw new HttpException(result);
    }

    private async Task<FileStatusResult> WaitForFileStatus(ChunkStatusResult chunkStatus)
    {
        while (true)
        {
            _logger.LogInformation("Checking file status of {Uuid}", chunkStatus.UUID);
            var data = await _client.GetFromJsonAsync<FileStatusResult>(
                $"https://upload.nexusmods.com/uploads/check_status?id={chunkStatus.UUID}");
            if (data!.FileChunksAssembled)
                return data;
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }
    

    public async Task<bool> IsPremium(CancellationToken token)
    {
        var validated = await ValidateCached(token);
        return validated.info.IsPremium;
    }

    /// <summary>
    /// Get OAuth token expiry information (if using OAuth, not API key)
    /// </summary>
    /// <returns>Token expiry info, or null if using API key or no token available</returns>
    public async Task<TokenExpiryInfo?> GetTokenExpiryInfo()
    {
        try
        {
            // Check encrypted file first
            var encryptedFilePath = JackifyConfig.GetDataDirectory().Combine("encrypted", "nexus-oauth-info");
            if (encryptedFilePath.FileExists())
            {
                var info = await AuthInfo.Get();
                if (info?.OAuth != null)
                {
                    var expiresAt = DateTime.FromFileTimeUtc(info.OAuth.ReceivedAt) + TimeSpan.FromSeconds(info.OAuth.ExpiresIn);
                    var timeUntilExpiry = expiresAt - DateTimeOffset.UtcNow;
                    var isExpiringSoon = IsTokenExpiringSoon(info.OAuth, TimeSpan.FromMinutes(15));
                    
                    return new TokenExpiryInfo
                    {
                        ExpiresAt = expiresAt,
                        TimeUntilExpiry = timeUntilExpiry,
                        IsExpiringSoon = isExpiringSoon,
                        Source = "encrypted_file"
                    };
                }
                else if (!string.IsNullOrWhiteSpace(info?.ApiKey))
                {
                    // Encrypted file contains API key, not OAuth
                    return new TokenExpiryInfo
                    {
                        ExpiresAt = null,
                        TimeUntilExpiry = null,
                        IsExpiringSoon = null,
                        Source = "encrypted_file (API key, not OAuth)"
                    };
                }
            }
            
            // Check NEXUS_OAUTH_INFO environment variable
            if (Environment.GetEnvironmentVariable("NEXUS_OAUTH_INFO") is { } oauthInfoJson)
            {
                try
                {
                    // Use in-memory state if available, otherwise parse from env var
                    if (_envVarOAuthState == null)
                    {
                        var info = JsonSerializer.Deserialize<NexusOAuthState>(oauthInfoJson, _jsonOptions);
                        if (info?.OAuth != null)
                        {
                            if (info.OAuth.ReceivedAt == 0)
                            {
                                info.OAuth.ReceivedAt = DateTime.UtcNow.ToFileTimeUtc();
                            }
                            _envVarOAuthState = info;
                        }
                    }
                    
                    if (_envVarOAuthState?.OAuth != null)
                    {
                        var expiresAt = DateTime.FromFileTimeUtc(_envVarOAuthState.OAuth.ReceivedAt) + TimeSpan.FromSeconds(_envVarOAuthState.OAuth.ExpiresIn);
                        var timeUntilExpiry = expiresAt - DateTimeOffset.UtcNow;
                        var isExpiringSoon = IsTokenExpiringSoon(_envVarOAuthState.OAuth, TimeSpan.FromMinutes(15));
                        
                        return new TokenExpiryInfo
                        {
                            ExpiresAt = expiresAt,
                            TimeUntilExpiry = timeUntilExpiry,
                            IsExpiringSoon = isExpiringSoon,
                            Source = "NEXUS_OAUTH_INFO"
                        };
                    }
                }
                catch
                {
                    // Failed to parse, continue to check other sources
                }
            }
            
            // Check NEXUS_API_KEY environment variable
            if (Environment.GetEnvironmentVariable("NEXUS_API_KEY") is { } apiKey)
            {
                if (apiKey.StartsWith("eyJ", StringComparison.Ordinal))
                {
                    // OAuth token in NEXUS_API_KEY (no refresh capability, can't determine expiry from JWT without parsing)
                    return new TokenExpiryInfo
                    {
                        ExpiresAt = null,
                        TimeUntilExpiry = null,
                        IsExpiringSoon = null,
                        Source = "NEXUS_API_KEY (OAuth token, expiry unknown - no refresh capability)"
                    };
                }
                else
                {
                    // Plain API key
                    return new TokenExpiryInfo
                    {
                        ExpiresAt = null,
                        TimeUntilExpiry = null,
                        IsExpiringSoon = null,
                        Source = "NEXUS_API_KEY (API key, not OAuth)"
                    };
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            // Log error but don't throw - return null to indicate no info available
            _logger.LogDebug(ex, "Error getting token expiry info");
            return null;
        }
    }

    /// <summary>
    /// Writes current token status to a JSON file that GUI can read during installs
    /// File location: ~/Jackify/token-status.json (or configured data directory)
    /// </summary>
    private async Task WriteTokenStatusFile(NexusOAuthState? state = null)
    {
        try
        {
            var statusFile = JackifyConfig.GetDataDirectory().Combine("token-status.json");
            var expiryInfo = await GetTokenExpiryInfo();
            
            var status = new
            {
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                token_info = expiryInfo != null ? new
                {
                    source = expiryInfo.Source,
                    expires_at = expiryInfo.ExpiresAt.HasValue ? new DateTimeOffset(expiryInfo.ExpiresAt.Value, TimeSpan.Zero).ToUnixTimeSeconds() : (long?)null,
                    time_until_expiry_seconds = expiryInfo.TimeUntilExpiry?.TotalSeconds,
                    is_expiring_soon = expiryInfo.IsExpiringSoon
                } : null
            };
            
            var json = JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(statusFile.ToString(), json);
        }
        catch (Exception ex)
        {
            // Don't fail if we can't write status file - it's just for GUI convenience
            _logger.LogDebug(ex, "Failed to write token status file");
        }
    }
}

/// <summary>
/// Information about OAuth token expiry
/// </summary>
public class TokenExpiryInfo
{
    /// <summary>
    /// When the token expires (UTC)
    /// </summary>
    public DateTime? ExpiresAt { get; set; }
    
    /// <summary>
    /// Time remaining until expiry
    /// </summary>
    public TimeSpan? TimeUntilExpiry { get; set; }
    
    /// <summary>
    /// Whether the token is expiring soon (within 15 minutes)
    /// </summary>
    public bool? IsExpiringSoon { get; set; }
    
    /// <summary>
    /// Source of the token (encrypted_file, NEXUS_OAUTH_INFO, etc.)
    /// </summary>
    public string Source { get; set; } = string.Empty;
}