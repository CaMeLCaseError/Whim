namespace Whim;

internal record WindowMinimizeStartedTransform(IWindow Window) : Transform
{
	internal override Result<Unit> Execute(
		IContext ctx,
		IInternalContext internalCtx,
		MutableRootSector mutableRootSector
	)
	{
		// Some apps - notably Windows Terminal - report a genuine OS-level minimize-start for a
		// window Whim just hid during a workspace switch, seemingly as their own reaction to being
		// hidden rather than anything the user did. If Whim tracked this as a real minimize, the
		// window would stay minimized (requiring a manual Alt+Tab to recover) the next time its
		// workspace is shown, instead of being displayed normally like every other window in it.
		// Ignoring it here means the layout engine never learns about it, so the next layout shows
		// the window normally via the existing ShowWindowNoActivate path (which restores a minimized
		// window without activating it) - no extra native calls needed.
		if (
			mutableRootSector.WindowSector.WhimHiddenWindows.TryGetValue(Window.Handle, out int hiddenAtTickCount)
			&& Environment.TickCount - hiddenAtTickCount < WindowFocusedTransform.WhimHiddenGracePeriodMs
		)
		{
			Logger.Debug($"Window {Window} was just hidden by Whim; ignoring spurious minimize-start");
			return Unit.Result;
		}

		Result<IWorkspace> workspaceResult = ctx.Store.Pick(PickWorkspaceByWindow(Window.Handle));
		if (!workspaceResult.TryGet(out IWorkspace workspace))
		{
			return Result.FromError<Unit>(workspaceResult.Error!);
		}

		ctx.Store.Dispatch(new MinimizeWindowStartTransform(workspace.Id, Window.Handle));

		mutableRootSector.WindowSector.QueueEvent(new WindowMinimizeStartedEventArgs() { Window = Window });

		return Unit.Result;
	}
}
