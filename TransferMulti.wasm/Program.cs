

namespace TransferMulti.wasm
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = WebAssemblyHostBuilder.CreateDefault(args);
            builder.RootComponents.Add<App>("#app");
            builder.RootComponents.Add<HeadOutlet>("head::after");
            builder.Services.AddDensenExtensions();
            builder.Services.AddStorages();
            builder.Services.AddSingleton<HashServiceFactory>();
            builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
#if DEBUG
            builder.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddJsonFile("appsettings.Developement.json", optional: true, reloadOnChange: true)
            .Build());
#else

            builder.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .Build());
#endif
            builder.Services.AddHttpClient("TransferMulti.srv", client =>
            {
                client.BaseAddress = new Uri(builder.Configuration.GetValue<string>("TransferMulti.srv") ?? throw new ArgumentException());
            });
            builder.Services.AddMudServices();
            builder.Services.AddMudMarkdownServices();
            await builder.Build().RunAsync();
        }
    }
}
