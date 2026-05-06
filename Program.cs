using FileOrganizer;
using FileOrganizer.Logging;
using FileOrganizer.Models;
using FileOrganizer.Services;

// Created before ConfigureLogging so the same instance is shared with DI.
HtmlLogBuffer? htmlLogBuffer = null;

IHost host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "FileOrganizer";
    })
    .ConfigureAppConfiguration((context, config) =>
    {
        // Support hot-reload of appsettings.json while service is running
        config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        config.AddJsonFile(
            $"appsettings.{context.HostingEnvironment.EnvironmentName}.json",
            optional: true,
            reloadOnChange: true);
    })
    .ConfigureLogging((context, logging) =>
    {
        htmlLogBuffer = new HtmlLogBuffer(context.Configuration);
        logging.AddProvider(new HtmlLoggerProvider(htmlLogBuffer));
    })
    .ConfigureServices((context, services) =>
    {
        services.Configure<FileOrganizerConfig>(
            context.Configuration.GetSection("FileOrganizer"));

        if (htmlLogBuffer is not null)
            services.AddSingleton(htmlLogBuffer);

        services.AddSingleton<ActivityReportService>();
        services.AddSingleton<FileProcessorService>();
        services.AddHostedService<Worker>();
        services.AddHostedService<DashboardServer>();
    })
    .Build();

await host.RunAsync();
