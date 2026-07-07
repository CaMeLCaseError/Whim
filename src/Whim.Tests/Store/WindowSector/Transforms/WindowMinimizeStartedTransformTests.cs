namespace Whim.Tests;

public class WindowMinimizeStartedTransformTests
{
	[Theory, AutoSubstituteData<StoreCustomization>]
	internal void NoWorkspaceForWindow(IContext ctx, MutableRootSector mutableRootSector, IWindow window)
	{
		// Given
		WindowMinimizeStartedTransform sut = new(window);

		// When
		Result<Unit>? result = null;
		CustomAssert.DoesNotRaise<WindowMinimizeStartedEventArgs>(
			h => mutableRootSector.WindowSector.WindowMinimizeStarted += h,
			h => mutableRootSector.WindowSector.WindowMinimizeStarted -= h,
			() => result = ctx.Store.Dispatch(sut)
		);

		// Then
		Assert.False(result!.Value.IsSuccessful);
	}

	[Theory, AutoSubstituteData<StoreCustomization>]
	internal void Success(IContext ctx, MutableRootSector rootSector, IWindow window)
	{
		// Given the window is in a workspace
		Workspace workspace = CreateWorkspace();
		PopulateThreeWayMap(rootSector, CreateMonitor((HMONITOR)1), workspace, window);
		rootSector.MapSector.WindowWorkspaceMap = rootSector.MapSector.WindowWorkspaceMap.Add(
			window.Handle,
			workspace.Id
		);

		WindowMinimizeStartedTransform sut = new(window);

		// When
		Result<Unit>? result = null;
		Assert.RaisedEvent<WindowMinimizeStartedEventArgs> ev;

		ev = Assert.Raises<WindowMinimizeStartedEventArgs>(
			h => rootSector.WindowSector.WindowMinimizeStarted += h,
			h => rootSector.WindowSector.WindowMinimizeStarted -= h,
			() =>
			{
				result = ctx.Store.Dispatch(sut);
			}
		);

		// Then
		Assert.True(result!.Value.IsSuccessful);
		Assert.Equal(window, ev.Arguments.Window);
		Assert.Contains(
			ctx.GetTransforms(),
			t => (t as MinimizeWindowStartTransform) == new MinimizeWindowStartTransform(workspace.Id, window.Handle)
		);
	}

	[Theory, AutoSubstituteData<StoreCustomization>]
	internal void RecentlyHiddenByWhim_MinimizeIgnored(IContext ctx, MutableRootSector rootSector, IWindow window)
	{
		// Given the window is in a workspace, and Whim deliberately hid it a moment ago - e.g.
		// Windows Terminal reporting a genuine minimize-start as its own reaction to being hidden
		// during a workspace switch, rather than anything the user did
		Workspace workspace = CreateWorkspace();
		PopulateThreeWayMap(rootSector, CreateMonitor((HMONITOR)1), workspace, window);
		rootSector.MapSector.WindowWorkspaceMap = rootSector.MapSector.WindowWorkspaceMap.Add(
			window.Handle,
			workspace.Id
		);
		rootSector.WindowSector.WhimHiddenWindows = rootSector.WindowSector.WhimHiddenWindows.SetItem(
			window.Handle,
			Environment.TickCount
		);

		WindowMinimizeStartedTransform sut = new(window);

		// When
		Result<Unit>? result = null;
		CustomAssert.DoesNotRaise<WindowMinimizeStartedEventArgs>(
			h => rootSector.WindowSector.WindowMinimizeStarted += h,
			h => rootSector.WindowSector.WindowMinimizeStarted -= h,
			() => result = ctx.Store.Dispatch(sut)
		);

		// Then the minimize is ignored (treated as a no-op success), and Whim's model never learns
		// the window is minimized - so it will be shown normally next time its workspace is laid out
		Assert.True(result!.Value.IsSuccessful);
		Assert.DoesNotContain(ctx.GetTransforms(), t => t is MinimizeWindowStartTransform);
	}
}
