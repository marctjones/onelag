using OneLag.Core;

namespace OneLag.Core.Tests;

public sealed class MovePlanExecutorTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "onelag-move-executor-tests", Guid.NewGuid().ToString("N"));

    public MovePlanExecutorTests()
    {
        Directory.CreateDirectory(tempRoot);
    }

    [Fact]
    public void MoveDryRunDoesNotMoveDirectory()
    {
        var source = CreateSourceTree();
        var destination = Path.Combine(tempRoot, "LocalDev", "project");

        var result = MovePlanExecutor.Move(
            new MoveExecutionOptions(source, destination, Execute: false, Acknowledged: false),
            CancellationToken.None);

        Assert.False(result.Executed);
        Assert.True(Directory.Exists(source));
        Assert.False(Directory.Exists(destination));
        Assert.Equal(1, result.FileCount);
    }

    [Fact]
    public void MoveExecuteVerifyAndRollbackRoundTrip()
    {
        var source = CreateSourceTree();
        var destination = Path.Combine(tempRoot, "LocalDev", "project");

        var move = MovePlanExecutor.Move(
            new MoveExecutionOptions(source, destination, Execute: true, Acknowledged: true),
            CancellationToken.None);

        Assert.True(move.Executed);
        Assert.False(Directory.Exists(source));
        Assert.True(Directory.Exists(destination));

        var verify = MovePlanExecutor.Verify(source, destination, 100_000, CancellationToken.None);

        Assert.True(verify.DestinationExists);
        Assert.Equal(1, verify.FileCount);

        var rollback = MovePlanExecutor.Rollback(
            new MoveExecutionOptions(source, destination, Execute: true, Acknowledged: true),
            CancellationToken.None);

        Assert.True(rollback.Executed);
        Assert.True(Directory.Exists(source));
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void MoveExecuteRequiresAcknowledgement()
    {
        var source = CreateSourceTree();
        var destination = Path.Combine(tempRoot, "LocalDev", "project");

        var ex = Assert.Throws<ArgumentException>(() => MovePlanExecutor.Move(
            new MoveExecutionOptions(source, destination, Execute: true, Acknowledged: false),
            CancellationToken.None));

        Assert.Contains("--i-understand-moves-files", ex.Message);
        Assert.True(Directory.Exists(source));
        Assert.False(Directory.Exists(destination));
    }

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private string CreateSourceTree()
    {
        var source = Path.Combine(tempRoot, "OneDrive", "project");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "file.txt"), "content");
        return source;
    }
}
