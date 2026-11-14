using dotmeer.filebrowser.Tasks;

namespace dotmeer.filebrowser.Tests;

public sealed class TaskNotificationTests
{
    [Fact]
    public void NotificationScope_FiltersTaskChangesAndHistoryCleanup()
    {
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var ownerView = TaskVisibility.ForUser(owner);
        var otherView = TaskVisibility.ForUser(other);
        var adminView = TaskVisibility.Everything;

        var ownerChange = new TaskChangeNotification(TaskChangeKind.Progress, Guid.NewGuid(), owner, TaskVisibility.ForUser(owner));
        Assert.True(ownerChange.Affects(ownerView));
        Assert.False(ownerChange.Affects(otherView));
        Assert.True(ownerChange.Affects(adminView));

        var ownerClear = new TaskChangeNotification(TaskChangeKind.HistoryCleared, null, Scope: ownerView);
        Assert.True(ownerClear.Affects(ownerView));
        Assert.False(ownerClear.Affects(otherView));
        Assert.True(ownerClear.Affects(adminView));

        var globalClear = new TaskChangeNotification(TaskChangeKind.HistoryCleared, null, Scope: adminView);
        Assert.True(globalClear.Affects(ownerView));
        Assert.True(globalClear.Affects(otherView));
        Assert.True(globalClear.Affects(adminView));
        Assert.False(globalClear.Affects(TaskVisibility.None));

        var legacyOrAnonymous = new TaskChangeNotification(TaskChangeKind.Updated, Guid.NewGuid());
        Assert.False(legacyOrAnonymous.Affects(ownerView));
        Assert.True(legacyOrAnonymous.Affects(adminView));
    }
}
