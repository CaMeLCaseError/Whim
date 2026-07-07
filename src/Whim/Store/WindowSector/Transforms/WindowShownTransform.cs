namespace Whim;

/// <summary>
/// Handles when an already-tracked window shows itself.
///
/// Some apps - notably Windows Terminal, which hosts all its windows in a single process - re-show
/// themselves shortly after Whim hides them during a workspace switch, without necessarily also
/// re-asserting foreground (which <see cref="WindowFocusedTransform"/> separately guards against).
/// Left alone, the window would be genuinely visible on an inactive workspace - e.g. leaking into
/// Alt+Tab from other workspaces.
/// </summary>
/// <param name="Window"></param>
internal record WindowShownTransform(IWindow Window) : Transform
{
	internal override Result<Unit> Execute(
		IContext ctx,
		IInternalContext internalCtx,
		MutableRootSector mutableRootSector
	)
	{
		// If Whim deliberately hid this window a moment ago, re-hide it: nothing legitimate re-shows
		// a window that fast (hidden windows have no taskbar button and do not appear in Alt+Tab). Any
		// resulting spurious minimize-start is separately ignored by WindowMinimizeStartedTransform.
		if (
			mutableRootSector.WindowSector.WhimHiddenWindows.TryGetValue(Window.Handle, out int hiddenAtTickCount)
			&& Environment.TickCount - hiddenAtTickCount < WindowFocusedTransform.WhimHiddenGracePeriodMs
		)
		{
			Logger.Debug($"Window {Window} was just hidden by Whim; re-hiding after it re-showed itself");
			ctx.NativeManager.HideWindow(Window.Handle);
		}

		return Unit.Result;
	}
}
