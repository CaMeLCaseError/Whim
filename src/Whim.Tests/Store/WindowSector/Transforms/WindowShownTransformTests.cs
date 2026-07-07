namespace Whim.Tests;

public class WindowShownTransformTests
{
	[Theory, AutoSubstituteData<StoreCustomization>]
	internal void NotRecentlyHidden_DoesNothing(IContext ctx, IWindow window)
	{
		// Given the window was not recently hidden by Whim
		WindowShownTransform sut = new(window);

		// When we dispatch the transform
		ctx.Store.Dispatch(sut);

		// Then the window is not re-hidden
		ctx.NativeManager.DidNotReceive().HideWindow(window.Handle);
	}

	[Theory, AutoSubstituteData<StoreCustomization>]
	internal void RecentlyHiddenByWhim_IsReHidden(IContext ctx, MutableRootSector rootSector, IWindow window)
	{
		// Given Whim deliberately hid the window a moment ago - e.g. Windows Terminal re-showing
		// itself right after being hidden during a workspace switch, without necessarily also
		// re-asserting foreground
		rootSector.WindowSector.WhimHiddenWindows = rootSector.WindowSector.WhimHiddenWindows.SetItem(
			window.Handle,
			Environment.TickCount
		);

		WindowShownTransform sut = new(window);

		// When we dispatch the transform
		ctx.Store.Dispatch(sut);

		// Then the window is re-hidden
		ctx.NativeManager.Received(1).HideWindow(window.Handle);
	}

	[Theory, AutoSubstituteData<StoreCustomization>]
	internal void HiddenLongAgo_DoesNotReHide(IContext ctx, MutableRootSector rootSector, IWindow window)
	{
		// Given Whim hid the window well outside the grace period - a genuine, unrelated show should
		// not be treated as spurious
		rootSector.WindowSector.WhimHiddenWindows = rootSector.WindowSector.WhimHiddenWindows.SetItem(
			window.Handle,
			Environment.TickCount - (WindowFocusedTransform.WhimHiddenGracePeriodMs * 2)
		);

		WindowShownTransform sut = new(window);

		// When we dispatch the transform
		ctx.Store.Dispatch(sut);

		// Then the window is not re-hidden
		ctx.NativeManager.DidNotReceive().HideWindow(window.Handle);
	}
}
