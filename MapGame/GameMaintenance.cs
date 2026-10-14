namespace MapGame;

public sealed class GameMaintenance(SinglePlayerGameManager games, LanRoomManager rooms) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            games.Cleanup();
            await rooms.CleanupAsync(stoppingToken);
        }
    }
}
