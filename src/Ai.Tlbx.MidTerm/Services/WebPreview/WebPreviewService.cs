using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Ai.Tlbx.MidTerm.Models.WebPreview;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Services.Browser;

namespace Ai.Tlbx.MidTerm.Services.WebPreview;

public sealed class WebPreviewService
{
    public const string DefaultPreviewName = "default";
    private const int MaxLogEntries = 100;

    private readonly int _serverPort;
    private readonly BrowserPreviewOriginService? _previewOriginService;
    private readonly string? _cookiesDirectory;
    private readonly ConcurrentDictionary<string, PreviewState> _previews = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _routeKeyToPreviewKey = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _leakedPathToRouteKey = new(StringComparer.OrdinalIgnoreCase);

    public WebPreviewService(int serverPort, string? cookiesDirectory = null)
        : this(serverPort, previewOriginService: null, cookiesDirectory)
    {
    }

    public WebPreviewService(
        int serverPort,
        BrowserPreviewOriginService? previewOriginService,
        string? cookiesDirectory = null)
    {
        _serverPort = serverPort;
        _previewOriginService = previewOriginService;
        _cookiesDirectory = cookiesDirectory;
    }

    public static string NormalizePreviewName(string? previewName)
    {
        return string.IsNullOrWhiteSpace(previewName)
            ? DefaultPreviewName
            : previewName.Trim();
    }

    public WebPreviewSessionInfo EnsurePreviewSession(string sessionId, string? previewName = null)
    {
        return ToInfo(GetOrCreateState(sessionId, previewName));
    }

    public WebPreviewSessionInfo? GetPreviewSession(string? sessionId, string? previewName = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        return TryGetState(sessionId, previewName, out var state)
            ? ToInfo(state)
            : null;
    }

    public WebPreviewSessionInfo? GetPreviewSessionByRouteKey(string routeKey)
    {
        return TryGetStateByRouteKey(routeKey, out var state)
            ? ToInfo(state)
            : null;
    }

    public WebPreviewSessionListResponse ListPreviewSessions(string sessionId)
    {
        var items = _previews.Values
            .Where(state => string.Equals(state.SessionId, sessionId, StringComparison.Ordinal))
            .OrderBy(state => string.Equals(state.PreviewName, DefaultPreviewName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(state => state.PreviewName, StringComparer.OrdinalIgnoreCase)
            .Select(ToInfo)
            .ToList();

        return new WebPreviewSessionListResponse
        {
            Previews = items
        };
    }

    public bool DeletePreviewSession(string sessionId, string? previewName = null)
    {
        var normalized = NormalizePreviewName(previewName);
        if (string.Equals(normalized, DefaultPreviewName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var key = BuildPreviewKey(sessionId, normalized);
        if (!_previews.TryRemove(key, out var state))
        {
            return false;
        }

        _routeKeyToPreviewKey.TryRemove(state.RouteKey, out _);
        ClearLeakedPathRoutes(state.RouteKey);
        state.Dispose();
        return true;
    }

    public void ClearSession(string sessionId)
    {
        var matches = _previews
            .Where(entry => string.Equals(entry.Value.SessionId, sessionId, StringComparison.Ordinal))
            .Select(entry => entry.Key)
            .ToArray();

        foreach (var key in matches)
        {
            if (_previews.TryRemove(key, out var state))
            {
                _routeKeyToPreviewKey.TryRemove(state.RouteKey, out _);
                ClearLeakedPathRoutes(state.RouteKey);
                state.Dispose();
            }
        }
    }

    public bool TryGetPreviewRouteKey(string sessionId, string? previewName, out string routeKey)
    {
        if (TryGetState(sessionId, previewName, out var state))
        {
            routeKey = state.RouteKey;
            return true;
        }

        routeKey = "";
        return false;
    }

    public bool TryGetTargetUriByRouteKey(string routeKey, out Uri? targetUri)
    {
        if (TryGetStateByRouteKey(routeKey, out var state))
        {
            targetUri = state.TargetUri;
            return true;
        }

        targetUri = null;
        return false;
    }

    public void RememberLeakedPathRoute(string routeKey, string path)
    {
        if (string.IsNullOrWhiteSpace(routeKey))
        {
            return;
        }

        var normalizedPath = NormalizeLeakedPath(path);
        if (string.IsNullOrEmpty(normalizedPath))
        {
            return;
        }

        _leakedPathToRouteKey[normalizedPath] = routeKey;
    }

    public bool TryGetRouteKeyByLeakedPath(string path, out string routeKey)
    {
        routeKey = "";
        var normalizedPath = NormalizeLeakedPath(path);
        if (string.IsNullOrEmpty(normalizedPath)
            || !_leakedPathToRouteKey.TryGetValue(normalizedPath, out var rememberedRouteKey)
            || string.IsNullOrEmpty(rememberedRouteKey))
        {
            return false;
        }

        routeKey = rememberedRouteKey;
        return true;
    }

    public string? GetTargetUrl(string sessionId, string? previewName = null)
    {
        return TryGetState(sessionId, previewName, out var state) ? state.TargetUrl : null;
    }

    public Uri? GetTargetUri(string sessionId, string? previewName = null)
    {
        return TryGetState(sessionId, previewName, out var state) ? state.TargetUri : null;
    }

    public Uri? GetTargetUriByRouteKey(string routeKey)
    {
        return TryGetStateByRouteKey(routeKey, out var state) ? state.TargetUri : null;
    }

    public bool IsActive(string sessionId, string? previewName = null)
    {
        return GetTargetUri(sessionId, previewName) is not null;
    }

    public bool HasAnyActivePreview()
    {
        return _previews.Values.Any(state => state.TargetUri is not null);
    }

    public bool SetTarget(string sessionId, string? previewName, string url, bool preserveCookies = false)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        url = NormalizeUrl(url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeFile)
        {
            if (!IsLocalFileUri(uri))
            {
                return false;
            }
        }
        else if (uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        var isMainServerTarget = IsMainServerTarget(uri);
        if (IsPreviewServerTarget(uri))
        {
            return false;
        }

        if (isMainServerTarget && (_previewOriginService?.IsEnabled != true))
        {
            return false;
        }

        var state = GetOrCreateState(sessionId, previewName);
        var oldTarget = state.TargetUri;
        if ((oldTarget is null || !TargetsShareCookieScope(oldTarget, uri)) && !preserveCookies)
        {
            ResetCookieJar(state);
        }

        state.TargetUri = uri;
        state.TargetUrl = uri.ToString();
        state.TargetRevision++;
        LoadCookiesFromDisk(state, uri);
        return true;
    }

    public void ClearTarget(string sessionId, string? previewName = null)
    {
        var state = GetOrCreateState(sessionId, previewName);
        state.TargetUrl = null;
        state.TargetUri = null;
        state.TargetRevision++;
        ResetCookieJar(state);
    }

    public bool HardReload(string sessionId, string? previewName = null)
    {
        return ClearCookiesInternal(GetOrCreateState(sessionId, previewName));
    }

    public bool ClearAllCookies(string sessionId, string? previewName = null)
    {
        return ClearCookiesInternal(GetOrCreateState(sessionId, previewName));
    }

    public bool ClearState(string sessionId, string? previewName = null)
    {
        var state = GetOrCreateState(sessionId, previewName);
        var target = state.TargetUri;
        if (target is not null)
        {
            DeleteCookieFile(state, target);
        }

        ResetCookieJar(state);
        ClearLog(state);
        ClearLeakedPathRoutes(state.RouteKey);
        state.TargetRevision++;
        return true;
    }

    public WebPreviewCookiesResponse GetCookies(string sessionId, string? previewName = null)
    {
        return TryGetState(sessionId, previewName, out var state)
            ? GetCookies(state)
            : new WebPreviewCookiesResponse();
    }

    public WebPreviewCookiesResponse GetBrowserCookies(string routeKey, Uri? requestUri = null)
    {
        return TryGetStateByRouteKey(routeKey, out var state)
            ? GetBrowserCookies(state, requestUri)
            : new WebPreviewCookiesResponse();
    }

    public bool SetCookieFromRaw(string routeKey, string rawCookie, Uri? requestUri = null, bool allowHttpOnly = true)
    {
        if (!TryGetStateByRouteKey(routeKey, out var state))
        {
            return false;
        }

        var target = requestUri ?? state.TargetUri;
        if (target is null || string.IsNullOrWhiteSpace(rawCookie))
        {
            return false;
        }

        if (!TryParseCookie(rawCookie, target, out var cookie))
        {
            return false;
        }

        if (!allowHttpOnly && cookie.HttpOnly)
        {
            cookie.HttpOnly = false;
        }

        lock (state.ClientLock)
        {
            try
            {
                if (string.IsNullOrEmpty(cookie.Domain))
                {
                    state.CookieContainer.Add(target, cookie);
                }
                else
                {
                    state.CookieContainer.Add(cookie);
                }

                PersistCookiesLocked(state, target);
                return true;
            }
            catch (CookieException)
            {
                return false;
            }
        }
    }

    public bool DeleteCookie(string sessionId, string? previewName, string name, string? path = null, string? domain = null)
    {
        if (!TryGetState(sessionId, previewName, out var state))
        {
            return false;
        }

        var target = state.TargetUri;
        if (target is null || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var cookie = new Cookie(name, "")
        {
            Path = string.IsNullOrWhiteSpace(path) ? "/" : path,
            Domain = string.IsNullOrWhiteSpace(domain) ? target.Host : domain.Trim(),
            Expires = DateTime.UtcNow.AddYears(-1)
        };

        lock (state.ClientLock)
        {
            try
            {
                state.CookieContainer.Add(cookie);
                PersistCookiesLocked(state, target);
                return true;
            }
            catch (CookieException)
            {
                return false;
            }
        }
    }

    public void ConfigureWebSocket(string routeKey, ClientWebSocket ws, Uri upstreamUri)
    {
        if (!TryGetStateByRouteKey(routeKey, out var state))
        {
            return;
        }

        ws.Options.RemoteCertificateValidationCallback = (sender, certificate, chain, errors) =>
            ValidateCertificate(state, sender, certificate, chain, errors);

        var httpScheme = upstreamUri.Scheme == "wss" ? "https" : "http";
        var cookieLookupUri = new UriBuilder(upstreamUri) { Scheme = httpScheme }.Uri;
        var cookieHeader = GetForwardedCookieHeader(routeKey, cookieLookupUri);
        if (!string.IsNullOrEmpty(cookieHeader))
        {
            ws.Options.SetRequestHeader("Cookie", cookieHeader);
        }
    }

    public PreviewHttpInvoker GetHttpClient(string routeKey)
    {
        return ResolveStateByRouteKey(routeKey).HttpClient;
    }

    public void PersistCookies(string routeKey)
    {
        if (!TryGetStateByRouteKey(routeKey, out var state))
        {
            return;
        }

        var target = state.TargetUri;
        if (target is null)
        {
            return;
        }

        lock (state.ClientLock)
        {
            PersistCookiesLocked(state, target);
        }
    }

    public void SyncSessionCookieForSelfTarget(string routeKey, string? token, Uri? target = null)
    {
        if (!TryGetStateByRouteKey(routeKey, out var state))
        {
            return;
        }

        var effectiveTarget = target ?? state.TargetUri;
        if (effectiveTarget is null
            || !IsMainServerTarget(effectiveTarget)
            || string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        var cookie = new Cookie(AuthService.SessionCookieName, token)
        {
            Domain = effectiveTarget.Host,
            Path = "/",
            Secure = effectiveTarget.Scheme == Uri.UriSchemeHttps,
            HttpOnly = true
        };

        lock (state.ClientLock)
        {
            try
            {
                state.CookieContainer.Add(effectiveTarget, cookie);
            }
            catch (CookieException)
            {
            }
        }
    }

    public void AddLogEntry(string routeKey, WebPreviewProxyLogEntry entry)
    {
        if (!TryGetStateByRouteKey(routeKey, out var state))
        {
            return;
        }

        entry.Id = Interlocked.Increment(ref state.LogIdCounter);
        entry.Timestamp = DateTimeOffset.UtcNow;
        state.ProxyLog.Enqueue(entry);
        while (state.ProxyLog.Count > MaxLogEntries)
        {
            state.ProxyLog.TryDequeue(out _);
        }
    }

    public string? GetForwardedCookieHeader(string routeKey, Uri requestUri)
    {
        if (!TryGetStateByRouteKey(routeKey, out var state))
        {
            return null;
        }

        lock (state.ClientLock)
        {
            var cookies = GetMatchingCookiesLocked(state, requestUri, includeHttpOnly: true);
            var header = BuildCookieHeader(cookies);
            return string.IsNullOrWhiteSpace(header) ? null : header;
        }
    }

    public void StoreResponseCookies(string routeKey, Uri responseUri, HttpResponseMessage response)
    {
        if (!TryGetStateByRouteKey(routeKey, out var state)
            || !response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            return;
        }

        lock (state.ClientLock)
        {
            foreach (var rawCookie in setCookies)
            {
                if (!TryParseCookie(rawCookie, responseUri, out var cookie))
                {
                    continue;
                }

                try
                {
                    if (string.IsNullOrEmpty(cookie.Domain))
                    {
                        state.CookieContainer.Add(responseUri, cookie);
                    }
                    else
                    {
                        state.CookieContainer.Add(cookie);
                    }
                }
                catch (CookieException)
                {
                }
            }

            PersistCookiesLocked(state, responseUri);
        }
    }

    public List<WebPreviewProxyLogEntry> GetLogEntries(string sessionId, string? previewName = null, int limit = MaxLogEntries)
    {
        return TryGetState(sessionId, previewName, out var state)
            ? GetLogEntries(state, limit)
            : [];
    }

    public void ClearLog(string sessionId, string? previewName = null)
    {
        if (TryGetState(sessionId, previewName, out var state))
        {
            ClearLog(state);
        }
    }

    public bool IsSelfTarget(Uri uri) => IsMainServerTarget(uri);

    public string BuildProxyPrefix(string routeKey) => $"/webpreview/{routeKey}";

    private bool ClearCookiesInternal(PreviewState state)
    {
        var target = state.TargetUri;
        if (target is null)
        {
            return false;
        }

        DeleteCookieFile(state, target);
        ResetCookieJar(state);
        return true;
    }

    private WebPreviewCookiesResponse GetCookies(PreviewState state)
    {
        var target = state.TargetUri;
        if (target is null)
        {
            return new WebPreviewCookiesResponse();
        }

        lock (state.ClientLock)
        {
            var cookies = state.CookieContainer.GetAllCookies();
            var result = new WebPreviewCookiesResponse
            {
                Header = state.CookieContainer.GetCookieHeader(target)
            };

            foreach (Cookie cookie in cookies)
            {
                result.Cookies.Add(ToCookieInfo(cookie));
            }

            return result;
        }
    }

    private WebPreviewCookiesResponse GetBrowserCookies(PreviewState state, Uri? requestUri = null)
    {
        var target = requestUri ?? state.TargetUri;
        if (target is null)
        {
            return new WebPreviewCookiesResponse();
        }

        lock (state.ClientLock)
        {
            var cookies = GetMatchingCookiesLocked(state, target, includeHttpOnly: false);
            var result = new WebPreviewCookiesResponse
            {
                Header = BuildCookieHeader(cookies)
            };

            foreach (var cookie in cookies)
            {
                result.Cookies.Add(ToCookieInfo(cookie));
            }

            return result;
        }
    }

    private static WebPreviewCookieInfo ToCookieInfo(Cookie cookie)
    {
        return new WebPreviewCookieInfo
        {
            Name = cookie.Name,
            Value = cookie.Value,
            Domain = cookie.Domain,
            Path = cookie.Path,
            Secure = cookie.Secure,
            HttpOnly = cookie.HttpOnly,
            ExpiresUtc = cookie.Expires == DateTime.MinValue
                ? null
                : new DateTimeOffset(cookie.Expires.ToUniversalTime())
        };
    }

    private List<WebPreviewProxyLogEntry> GetLogEntries(PreviewState state, int limit)
    {
        var entries = state.ProxyLog.ToArray();
        if (limit >= entries.Length)
        {
            return entries.ToList();
        }

        return entries[^limit..].ToList();
    }

    private void ClearLog(PreviewState state)
    {
        while (state.ProxyLog.TryDequeue(out _))
        {
        }
    }

    private WebPreviewSessionInfo ToInfo(PreviewState state)
    {
        return new WebPreviewSessionInfo
        {
            SessionId = state.SessionId,
            PreviewName = state.PreviewName,
            RouteKey = state.RouteKey,
            Url = state.TargetUrl,
            Active = state.TargetUri is not null,
            TargetRevision = state.TargetRevision
        };
    }

    private PreviewState GetOrCreateState(string sessionId, string? previewName)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("sessionId is required.", nameof(sessionId));
        }

        var normalizedName = NormalizePreviewName(previewName);
        var key = BuildPreviewKey(sessionId, normalizedName);
        return _previews.GetOrAdd(key, _ =>
        {
            var routeKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(10));
            var state = new PreviewState(
                sessionId.Trim(),
                normalizedName,
                routeKey,
                CreateHttpClientForState);
            _routeKeyToPreviewKey[routeKey] = key;
            return state;
        });
    }

    private bool TryGetState(string sessionId, string? previewName, out PreviewState state)
    {
        state = null!;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var key = BuildPreviewKey(sessionId, NormalizePreviewName(previewName));
        if (_previews.TryGetValue(key, out var existing))
        {
            state = existing;
            return true;
        }

        return false;
    }

    private bool TryGetStateByRouteKey(string routeKey, out PreviewState state)
    {
        state = null!;
        if (string.IsNullOrWhiteSpace(routeKey))
        {
            return false;
        }

        if (!_routeKeyToPreviewKey.TryGetValue(routeKey, out var previewKey))
        {
            return false;
        }

        if (_previews.TryGetValue(previewKey, out var existing))
        {
            state = existing;
            return true;
        }

        return false;
    }

    private PreviewState ResolveStateByRouteKey(string routeKey)
    {
        if (!TryGetStateByRouteKey(routeKey, out var state))
        {
            throw new KeyNotFoundException($"Unknown web preview route '{routeKey}'.");
        }

        return state;
    }

    private void ClearLeakedPathRoutes(string routeKey)
    {
        foreach (var entry in _leakedPathToRouteKey)
        {
            if (string.Equals(entry.Value, routeKey, StringComparison.Ordinal)
                && _leakedPathToRouteKey.TryGetValue(entry.Key, out var currentRouteKey)
                && string.Equals(currentRouteKey, routeKey, StringComparison.Ordinal))
            {
                _leakedPathToRouteKey.TryRemove(entry.Key, out _);
            }
        }
    }

    private static string NormalizeLeakedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        var normalized = path.Trim();
        if (!normalized.StartsWith("/", StringComparison.Ordinal))
        {
            normalized = "/" + normalized;
        }

        var queryIndex = normalized.IndexOfAny(['?', '#']);
        if (queryIndex >= 0)
        {
            normalized = normalized[..queryIndex];
        }

        return normalized;
    }

    private static string BuildPreviewKey(string sessionId, string previewName)
    {
        return $"{sessionId.Trim()}\n{previewName.Trim().ToLowerInvariant()}";
    }

    private void ResetCookieJar(PreviewState state)
    {
        lock (state.ClientLock)
        {
            var oldClient = state.HttpClient;
            state.CookieContainer = new CookieContainer();
            state.HttpClient = CreateHttpClientForState(state, state.CookieContainer);
            oldClient.Dispose();
        }
    }

    private PreviewHttpInvoker CreateHttpClientForState(PreviewState state, CookieContainer cookieContainer)
    {
        _ = cookieContainer;
        SocketsHttpHandler? handler = new()
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = static (context, cancellationToken) =>
                ConnectPreviewSocketAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, cancellationToken),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (sender, certificate, chain, errors) =>
                    ValidateCertificate(state, sender, certificate, chain, errors)
            }
        };

        try
        {
            var invoker = new PreviewHttpInvoker(handler, TimeSpan.FromMinutes(5));
            handler = null;
            return invoker;
        }
        finally
        {
            handler?.Dispose();
        }
    }

    private bool ValidateCertificate(
        PreviewState state,
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        _ = sender;
        _ = certificate;
        _ = chain;

        return ShouldAcceptPreviewCertificate(state.TargetUri, sslPolicyErrors);
    }

    internal static bool ShouldAcceptPreviewCertificate(Uri? target, SslPolicyErrors sslPolicyErrors)
    {
        if (sslPolicyErrors == SslPolicyErrors.None)
        {
            return true;
        }

        return target is not null && target.Scheme == Uri.UriSchemeHttps;
    }

    internal static async ValueTask<Stream> ConnectPreviewSocketAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        var addresses = await ResolvePreviewConnectAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        var perAddressTimeout = addresses.Length > 1
            ? TimeSpan.FromSeconds(2)
            : TimeSpan.FromSeconds(10);
        Exception? lastError = null;

        foreach (var address in addresses)
        {
            Socket? socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            try
            {
                socket.NoDelay = true;
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCts.CancelAfter(perAddressTimeout);
                await socket.ConnectAsync(new IPEndPoint(address, port), attemptCts.Token).ConfigureAwait(false);
                var stream = new NetworkStream(socket, ownsSocket: true);
                socket = null;
                return stream;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                lastError = ex;
            }
            finally
            {
                socket?.Dispose();
            }
        }

        throw lastError ?? new SocketException((int)SocketError.HostNotFound);
    }

    private static async Task<IPAddress[]> ResolvePreviewConnectAddressesAsync(
        string host,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host.AsSpan(), out var parsed))
        {
            return [parsed];
        }

        return await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
    }

    private void PersistCookiesLocked(PreviewState state, Uri target)
    {
        var filePath = GetCookieFilePath(state, target);
        if (filePath is null)
        {
            return;
        }

        try
        {
            var cookies = state.CookieContainer.GetAllCookies();
            var lines = new List<string>();
            foreach (Cookie cookie in cookies)
            {
                if (cookie.Expired)
                {
                    continue;
                }

                if (!ShouldPersistCookie(target, cookie))
                {
                    continue;
                }

                lines.Add(FormatCookie(cookie));
            }

            if (lines.Count == 0)
            {
                File.Delete(filePath);
                return;
            }

            Directory.CreateDirectory(_cookiesDirectory!);
            File.WriteAllLines(filePath, lines);
        }
        catch
        {
        }
    }

    private void LoadCookiesFromDisk(PreviewState state, Uri target)
    {
        var filePath = GetCookieFilePath(state, target);
        if (filePath is null || !File.Exists(filePath))
        {
            return;
        }

        try
        {
            var lines = File.ReadAllLines(filePath);
            lock (state.ClientLock)
            {
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    if (!TryParseCookie(line, target, out var cookie))
                    {
                        continue;
                    }

                    if (cookie.Expired || !ShouldPersistCookie(target, cookie))
                    {
                        continue;
                    }

                    try
                    {
                        if (string.IsNullOrEmpty(cookie.Domain))
                        {
                            state.CookieContainer.Add(target, cookie);
                        }
                        else
                        {
                            state.CookieContainer.Add(cookie);
                        }
                    }
                    catch (CookieException)
                    {
                    }
                }
            }
        }
        catch
        {
        }
    }

    private void DeleteCookieFile(PreviewState state, Uri target)
    {
        var filePath = GetCookieFilePath(state, target);
        if (filePath is null)
        {
            return;
        }

        try
        {
            File.Delete(filePath);
        }
        catch
        {
        }
    }

    private string? GetCookieFilePath(PreviewState state, Uri target)
    {
        if (_cookiesDirectory is null)
        {
            return null;
        }

        var host = target.Host.Replace(':', '_');
        var previewKey = SanitizeCookieFileSegment($"{state.SessionId}_{state.PreviewName}");
        var fileName = string.Create(CultureInfo.InvariantCulture, $"{previewKey}_{host}_{target.Port}.txt");
        return Path.Combine(_cookiesDirectory, fileName);
    }

    private static string SanitizeCookieFileSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value
            .Select(ch => invalid.Contains(ch) ? '_' : ch)
            .ToArray();
        return new string(chars);
    }

    private static string FormatCookie(Cookie cookie)
    {
        var parts = new List<string>
        {
            $"{cookie.Name}={cookie.Value}",
            $"Domain={cookie.Domain}",
            $"Path={cookie.Path}"
        };

        if (cookie.Secure)
        {
            parts.Add("Secure");
        }

        if (cookie.HttpOnly)
        {
            parts.Add("HttpOnly");
        }

        if (cookie.Expires != DateTime.MinValue)
        {
            parts.Add($"Expires={cookie.Expires.ToUniversalTime():R}");
        }

        return string.Join("; ", parts);
    }

    private List<Cookie> GetMatchingCookiesLocked(PreviewState state, Uri requestUri, bool includeHttpOnly)
    {
        var cookies = new List<Cookie>();
        foreach (Cookie cookie in state.CookieContainer.GetAllCookies())
        {
            if (cookie.Expired)
            {
                continue;
            }

            if (!includeHttpOnly && cookie.HttpOnly)
            {
                continue;
            }

            if (!CookieMatchesRequestUri(cookie, requestUri))
            {
                continue;
            }

            cookies.Add(cookie);
        }

        cookies.Sort((a, b) =>
        {
            var pathCompare = (b.Path?.Length ?? 0).CompareTo(a.Path?.Length ?? 0);
            return pathCompare != 0
                ? pathCompare
                : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
        });

        return cookies;
    }

    private static string BuildCookieHeader(IEnumerable<Cookie> cookies)
    {
        return string.Join("; ", cookies.Select(cookie => $"{cookie.Name}={cookie.Value}"));
    }

    private static bool CookieMatchesRequestUri(Cookie cookie, Uri requestUri)
    {
        if (cookie.Secure && requestUri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (!DomainMatches(cookie.Domain, requestUri.Host))
        {
            return false;
        }

        return PathMatches(cookie.Path, requestUri.AbsolutePath);
    }

    private static bool DomainMatches(string cookieDomain, string requestHost)
    {
        var normalizedDomain = cookieDomain.Trim().TrimStart('.');
        if (string.IsNullOrEmpty(normalizedDomain))
        {
            return false;
        }

        return requestHost.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase)
            || requestHost.EndsWith("." + normalizedDomain, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathMatches(string cookiePath, string requestPath)
    {
        var normalizedCookiePath = string.IsNullOrWhiteSpace(cookiePath) ? "/" : cookiePath;
        if (!normalizedCookiePath.StartsWith("/", StringComparison.Ordinal))
        {
            normalizedCookiePath = "/" + normalizedCookiePath;
        }

        var normalizedRequestPath = string.IsNullOrEmpty(requestPath) ? "/" : requestPath;
        if (!normalizedRequestPath.StartsWith("/", StringComparison.Ordinal))
        {
            normalizedRequestPath = "/" + normalizedRequestPath;
        }

        if (normalizedRequestPath.Equals(normalizedCookiePath, StringComparison.Ordinal))
        {
            return true;
        }

        if (!normalizedRequestPath.StartsWith(normalizedCookiePath, StringComparison.Ordinal))
        {
            return false;
        }

        return normalizedCookiePath.EndsWith("/", StringComparison.Ordinal)
            || normalizedRequestPath.Length > normalizedCookiePath.Length
            && normalizedRequestPath[normalizedCookiePath.Length] == '/';
    }

    private bool IsMainServerTarget(Uri uri)
    {
        return IsThisServerTarget(uri, _serverPort);
    }

    private bool IsPreviewServerTarget(Uri uri)
    {
        return _previewOriginService is { IsEnabled: true }
            && IsThisServerTarget(uri, _previewOriginService.PreviewPort);
    }

    private bool IsThisServerTarget(Uri uri, int port)
    {
        if (uri.Port != port)
        {
            return false;
        }

        if (IsLocalAddress(uri.Host) || uri.Host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string? dnsHostName = null;
        try
        {
            dnsHostName = Dns.GetHostName();
        }
        catch (SocketException)
        {
        }

        if (!string.IsNullOrEmpty(dnsHostName)
            && uri.Host.Equals(dnsHostName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var address in ResolveHostAddresses(uri.Host))
        {
            if (IPAddress.IsLoopback(address))
            {
                return true;
            }

            foreach (var localAddress in ResolveLocalAddresses(dnsHostName))
            {
                if (address.Equals(localAddress))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool ShouldPersistCookie(Uri target, Cookie cookie)
    {
        return !(IsMainServerTarget(target)
            && string.Equals(cookie.Name, AuthService.SessionCookieName, StringComparison.Ordinal));
    }

    private static IEnumerable<IPAddress> ResolveHostAddresses(string host)
    {
        if (IPAddress.TryParse(host.AsSpan(), out var parsed))
        {
            yield return parsed;
            yield break;
        }

        IPAddress[] addresses;
        try
        {
            addresses = Dns.GetHostAddresses(host);
        }
        catch (SocketException)
        {
            yield break;
        }
        catch (ArgumentException)
        {
            yield break;
        }

        foreach (var address in addresses)
        {
            yield return address;
        }
    }

    private static IEnumerable<IPAddress> ResolveLocalAddresses(string? dnsHostName)
    {
        if (string.IsNullOrEmpty(dnsHostName))
        {
            yield break;
        }

        IPAddress[] addresses;
        try
        {
            addresses = Dns.GetHostAddresses(dnsHostName);
        }
        catch (SocketException)
        {
            yield break;
        }

        foreach (var address in addresses)
        {
            yield return address;
        }
    }

    private static bool TargetsShareCookieScope(Uri left, Uri right)
    {
        return left.Authority.Equals(right.Authority, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLocalAddress(string host)
    {
        return host is "localhost" or "127.0.0.1" or "::1"
            || host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLocalFileUri(Uri uri)
    {
        if (!uri.IsFile)
        {
            return false;
        }

        var host = uri.Host;
        return string.IsNullOrWhiteSpace(host)
            || IsLocalAddress(host)
            || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeUrl(string url)
    {
        url = url.Trim();

        if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        if (!url.Contains("://", StringComparison.Ordinal))
        {
            if (url.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("127.0.0.1", StringComparison.Ordinal)
                || url.StartsWith("[::1]", StringComparison.Ordinal))
            {
                url = "http://" + url;
            }
            else
            {
                url = "https://" + url;
            }
        }

        return url;
    }

    private static bool TryParseCookie(string rawCookie, Uri target, out Cookie cookie)
    {
        cookie = new Cookie();
        var parts = rawCookie.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var first = parts[0];
        var eqIdx = first.IndexOf('=', StringComparison.Ordinal);
        if (eqIdx <= 0)
        {
            return false;
        }

        var name = first[..eqIdx].Trim();
        var value = first[(eqIdx + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        cookie = new Cookie(name, value)
        {
            Domain = target.Host,
            Path = "/",
            Secure = target.Scheme == "https"
        };

        for (var i = 1; i < parts.Length; i++)
        {
            var part = parts[i];
            var idx = part.IndexOf('=', StringComparison.Ordinal);
            var key = (idx >= 0 ? part[..idx] : part).Trim();
            var attrValue = idx >= 0 ? part[(idx + 1)..].Trim() : "";

            if (key.Equals("Path", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(attrValue))
                {
                    cookie.Path = attrValue;
                }
            }
            else if (key.Equals("Domain", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(attrValue))
                {
                    cookie.Domain = attrValue.TrimStart('.');
                }
            }
            else if (key.Equals("Secure", StringComparison.OrdinalIgnoreCase))
            {
                cookie.Secure = true;
            }
            else if (key.Equals("HttpOnly", StringComparison.OrdinalIgnoreCase))
            {
                cookie.HttpOnly = true;
            }
            else if (key.Equals("Expires", StringComparison.OrdinalIgnoreCase))
            {
                if (DateTime.TryParse(attrValue, CultureInfo.InvariantCulture, DateTimeStyles.None, out var expires))
                {
                    cookie.Expires = expires.ToUniversalTime();
                }
            }
            else if (key.Equals("Max-Age", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(attrValue, CultureInfo.InvariantCulture, out var seconds))
                {
                    cookie.Expires = DateTime.UtcNow.AddSeconds(seconds);
                }
            }
        }

        return true;
    }

    private sealed class PreviewState : IDisposable
    {
        public string SessionId { get; }
        public string PreviewName { get; }
        public string RouteKey { get; }
        public object ClientLock { get; } = new();
        public ConcurrentQueue<WebPreviewProxyLogEntry> ProxyLog { get; } = new();
        public int LogIdCounter;
        public string? TargetUrl;
        public Uri? TargetUri;
        public long TargetRevision;
        public CookieContainer CookieContainer { get; set; }
        public PreviewHttpInvoker HttpClient { get; set; }

        public PreviewState(
            string sessionId,
            string previewName,
            string routeKey,
            Func<PreviewState, CookieContainer, PreviewHttpInvoker> clientFactory)
        {
            SessionId = sessionId;
            PreviewName = previewName;
            RouteKey = routeKey;
            CookieContainer = new CookieContainer();
            HttpClient = clientFactory(this, CookieContainer);
        }

        public void Dispose()
        {
            HttpClient.Dispose();
        }
    }

    public sealed class PreviewHttpInvoker : IDisposable
    {
        private readonly HttpMessageInvoker _invoker;
        private readonly TimeSpan _timeout;

        public PreviewHttpInvoker(SocketsHttpHandler handler, TimeSpan timeout)
        {
            _invoker = new HttpMessageInvoker(handler, disposeHandler: true);
            _timeout = timeout;
        }

        public async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            HttpCompletionOption completionOption,
            CancellationToken cancellationToken)
        {
            _ = completionOption;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_timeout);
            return await _invoker.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        }

        public async Task<string> GetStringAsync(string uri, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _invoker.Dispose();
        }
    }
}
