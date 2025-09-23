using SingleInstanceProgramNS;
using ArgumentParserNS;
using System.Text;
using System.Dynamic;

SingleInstanceProgram s = SingleInstanceProgram.Create("UniqueId", args);
ArgumentParser argparse = new ArgumentParser();

argparse.AddArgument("command")
    .WithType(typeof(string))
    .WithHelp("Either provide start or stop as an argument");

argparse.AddArgument("-p", "--port")
    .WithType(typeof(int))
    .WithHelp("The port on which the reverse proxy will run.");

argparse.AddArgument("-o", "--origin")
    .WithType(typeof(string))
    .WithHelp("The server to which requests will be forwarded.");

argparse.AddArgument("-c", "--clear-cache")
    .WithParserAction(ParserAction.store_true)
    .WithHelp("Clears the cache.");

argparse.AddArgument("--print-cache")
    .WithParserAction(ParserAction.store_true)
    .WithHelp("Prints the cache");


IDictionary<string,CachedResponse> cache = new Dictionary<string,CachedResponse>();
WebApplication? app = null;
async void s_MessageReceivedFromOtherInstance(object? sender,  MessageReceivedEventArgs e)
{
    if (e.Message != null)
    {
        string[] response = (await processArguments(e.Message)).Split('\n');
        e.RespondToOtherSender?.Invoke(response);
    }
}

void s_MessageReceivedFromFirstInstance(object? sender, MessageReceivedEventArgs e)
{
    if(e.Message != null)
    {
        foreach(string s in e.Message)
        {
            Console.WriteLine(s);
        }
    }
}



s.MessageReceivedFromOtherInstance += s_MessageReceivedFromOtherInstance;
s.MessageReceivedFromFirstInstance += s_MessageReceivedFromFirstInstance;
s.Start();

#if DEBUG
Console.WriteLine(processArguments("start -o 127.0.0.1:5064 -p 2121".Split()));
#else
Console.WriteLine(processArguments(args));
#endif


async Task<string> processArguments(string[] args)
{
    dynamic expando = argparse.ArgParse(args);

    if(ArgumentParser.HasProperty(expando, "err_msg"))
    {
        return $"{expando.err_msg}\n{argparse.GetHelpMessage()}\nTo use the app, either use 'server start -p <port> -o <origin-ip>' 'server stop' 'server --clear-cache' 'server --print-cache'";
    }

    if(expando.clear_cache == true)
    {
        try
        {
            cache.Clear();
            return "Cleared the Cache";
        }
        catch
        {
            return "Failed to clear the cache";
        }
        
    }

    if (expando.print_cache == true)
    {
        try
        {
            StringBuilder sb = new StringBuilder();
            int i = 0;
            foreach (KeyValuePair<string, CachedResponse> k in cache)
            {
                sb.Append($"Entry {i}:\t{k.Key}\n");
                sb.Append(k.Value.ToString() + "\n");
                sb.Append("-----------------------------------------------------------------\n");
                i++;
            }

            Console.WriteLine(sb.ToString());
            return sb.ToString();
        }
        catch
        {
            return "Encountered an error while getting the cache.";
        }
        
    }

    if (ArgumentParser.HasProperty(expando,"command"))
    {
        if(expando.command == "start" && ArgumentParser.HasProperty(expando, "port") && ArgumentParser.HasProperty(expando, "origin"))
        {
            StartServer(expando.port, expando.origin);
            return "Started the server...";
        }
        else if (expando.command == "stop")
        {
            if (app != null)
            {
                await app.StopAsync();
                return "Shutting down the server...";
            }
            else
            {
                return "The server isn't running";
            }
        }

        return "unknown command";
    }
    else
    {
        return "To use the app, either use 'server start -p <port> -o <origin-ip>' 'server stop' 'server --clear-cache' 'server --print-cache'";
    }
}



void StartServer(int port, string originIP)
{
    var builder = WebApplication.CreateBuilder();

    builder.WebHost.ConfigureKestrel((context, serverOptions) =>
    {
        serverOptions.Listen(System.Net.IPAddress.Any, port);
    });

    app = builder.Build();
    var handler = new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    };
    HttpClient client = new HttpClient(handler);

    app.Run(async context =>
    {
        var key = context.Request.Path.ToString() + context.Request.QueryString.ToString();
        if (cache.ContainsKey(key))
        {
            Console.WriteLine("X-Cache: HIT");
            context.Response.Headers["X-Cache"] = "HIT";
        }
        else
        {
            var uri = new UriBuilder(originIP)
            {
                Path = context.Request.Path.ToString(),
                Query = context.Request.QueryString.ToString().TrimStart('?')
            }.Uri;
            HttpRequestMessage msg = context.CreateProxyHttpRequest(uri);
            using HttpResponseMessage res = await client.SendAsync(msg);

            Console.WriteLine("X-Cache: MISS");
            context.Response.Headers["X-Cache"] = "MISS";
            cache[key] = await res.ToCacheResponseAsync();
        }
        await context.WriteCachedResponse(cache[key]);
    });
    app.Run();
}

////Taken from: https://github.com/aspnet/Proxy/blob/master/src/Microsoft.AspNetCore.Proxy/ProxyAdvancedExtensions.cs
static class HttpRequestExtensions
{

    public static HttpRequestMessage CreateProxyHttpRequest(this HttpContext context, Uri uri)
    {
        var request = context.Request;

        var requestMessage = new HttpRequestMessage();
        var requestMethod = request.Method;
        if (!HttpMethods.IsGet(requestMethod) &&
            !HttpMethods.IsHead(requestMethod) &&
            !HttpMethods.IsDelete(requestMethod) &&
            !HttpMethods.IsTrace(requestMethod))
        {
            var streamContent = new StreamContent(request.Body);
            requestMessage.Content = streamContent;
        }

        // Copy the request headers
        foreach (var header in request.Headers)
        {
            if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()) && requestMessage.Content != null)
            {
                requestMessage.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        requestMessage.Headers.Host = uri.Authority;
        requestMessage.RequestUri = uri;
        requestMessage.Method = new HttpMethod(request.Method);

        return requestMessage;
    }
    ////End of the code taken from: https://github.com/aspnet/Proxy/blob/master/src/Microsoft.AspNetCore.Proxy/ProxyAdvancedExtensions.cs
    public static async Task WriteCachedResponse(this HttpContext context, CachedResponse cached)
    {
        if(cached == null)
        {
            throw new ArgumentNullException(nameof(cached));
        }

        var response = context.Response;

        response.StatusCode = (int)cached.StatusCode;
        foreach (var header in cached.Headers)
        {
            response.Headers[header.Key] = header.Value;
        }

        response.Headers.Remove("transfer-encoding");

        await response.Body.WriteAsync(cached.Body, 0, cached.Body.Length, context.RequestAborted);
    }

    public static async Task<CachedResponse> ToCacheResponseAsync(this HttpResponseMessage res)
    {
        var cached = new CachedResponse
        {
            StatusCode = (int)res.StatusCode,
            Headers = res.Headers
                .Concat(res.Content.Headers)
                .ToDictionary(x => x.Key, x => x.Value.ToArray())
        };

        cached.Body = await res.Content.ReadAsByteArrayAsync();
        return cached;
    }
}

class CachedResponse
{
    public int StatusCode { get; set; }
    public Dictionary<string, string[]> Headers { get; set; } = new();
    public byte[] Body { get; set; } = Array.Empty<byte>();

    public override string ToString()
    {
        StringBuilder sb = new StringBuilder();
        foreach (var header in Headers)
        {
            sb.Append(header.Key);
            sb.Append(": ");
            sb.Append(string.Join("\n",header.Value));
        }
        sb.Append("\n\n");
        sb.Append(Encoding.Default.GetString(Body));
        sb.Append("\n\n");
        sb.Append(StatusCode.ToString());
        return sb.ToString();
    }
}
