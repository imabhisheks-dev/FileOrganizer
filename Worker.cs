using FileOrganizer.Models;
using FileOrganizer.Services;
using Microsoft.Extensions.Options;

namespace FileOrganizer;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly FileProcessorService _fileProcessor;
    private readonly IOptionsMonitor<FileOrganizerConfig> _config;

    public Worker(
        ILogger<Worker> logger,
        FileProcessorService fileProcessor,
        IOptionsMonitor<FileOrganizerConfig> config)
    {
        _logger = logger;
        _fileProcessor = fileProcessor;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("FileOrganizer Service started at: {time}", DateTimeOffset.Now);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _fileProcessor.ProcessAllRulesAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Graceful shutdown — no need to log
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error while processing file rules.");
            }

            var intervalSeconds = Math.Max(5, _config.CurrentValue.PollingIntervalSeconds);
            var interval = TimeSpan.FromSeconds(intervalSeconds);
            _logger.LogInformation("Next scan in {seconds} second(s).", intervalSeconds);

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("FileOrganizer Service stopped at: {time}", DateTimeOffset.Now);
    }
}
