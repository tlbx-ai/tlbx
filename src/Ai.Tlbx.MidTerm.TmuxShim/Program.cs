using System.Text;

namespace Ai.Tlbx.MidTerm.TmuxShim;

public static class Program
{
    private static readonly HttpClientHandler HttpHandler = new()
    {
        ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    };

    private static readonly HttpClient HttpClient = new(HttpHandler);

    public static async Task<int> Main(string[] args)
    {
        if (args.Any(a => a is "-V" or "-v" or "--version"))
        {
            Console.WriteLine("tmux 3.5a-midterm");
            return 0;
        }

        var endpoint = ResolveEndpoint();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            Console.Error.WriteLine("midterm tmux shim: missing endpoint");
            return 1;
        }

        try
        {
            using var body = new ByteArrayContent(EncodeArgs(args));
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = body
            };

            var token = Environment.GetEnvironmentVariable("MT_TOKEN");
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Add("Cookie", $"mm-session={token}");
            }

            var pane = Environment.GetEnvironmentVariable("TMUX_PANE");
            if (!string.IsNullOrWhiteSpace(pane))
            {
                request.Headers.Add("X-Tmux-Pane", pane);
            }

            using var response = await HttpClient.SendAsync(request).ConfigureAwait(false);
            var output = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            Console.Write(output);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"midterm tmux shim: {ex.Message}");
            return 1;
        }
    }

    private static string? ResolveEndpoint()
    {
        var fromEnv = Environment.GetEnvironmentVariable("MIDTERM_TMUX_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        var endpointFile = Path.Combine(AppContext.BaseDirectory, "tmux.endpoint");
        return File.Exists(endpointFile)
            ? File.ReadAllText(endpointFile, Encoding.UTF8).Trim()
            : null;
    }

    private static byte[] EncodeArgs(string[] args)
    {
        using var stream = new MemoryStream();
        foreach (var arg in args)
        {
            var bytes = Encoding.UTF8.GetBytes(arg);
            stream.Write(bytes, 0, bytes.Length);
            stream.WriteByte(0);
        }

        return stream.ToArray();
    }
}
