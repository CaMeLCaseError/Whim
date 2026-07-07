namespace Whim;

/// <summary>
/// Moves the window with <paramref name="WindowHandle"/> to the given <paramref name="TargetWorkspaceId"/>.
/// </summary>
/// <param name="TargetWorkspaceId">
/// The id of the workspace to move the window to.
/// </param>
/// <param name="WindowHandle">
/// The window to move. If <see langword="null"/>, this will default to
/// the focused/active window.
/// </param>
/// <param name="FocusWindow">
/// When <see langword="true"/> (the default), the moved window is followed: if the target
/// workspace isn't currently visible, Whim switches to it and focuses the window. When
/// <see langword="false"/>, the window is moved to the target workspace without switching to it -
/// the window is hidden, the current workspace is re-laid out, and focus stays where it is.
/// </param>
public record MoveWindowToWorkspaceTransform(
	WorkspaceId TargetWorkspaceId,
	HWND WindowHandle = default,
	bool FocusWindow = true
) : Transform
{
	internal override Result<Unit> Execute(IContext ctx, IInternalContext internalCtx, MutableRootSector rootSector)
	{
		Result<IWorkspace> targetWorkspaceResult = ctx.Store.Pick(PickWorkspaceById(TargetWorkspaceId));
		if (!targetWorkspaceResult.TryGet(out IWorkspace targetWorkspace))
		{
			return Result.FromError<Unit>(targetWorkspaceResult.Error!);
		}

		// Get the window.
		HWND windowHandle = WindowHandle.OrLastFocusedWindow(ctx);
		if (windowHandle == default)
		{
			return Result.FromError<Unit>(StoreErrors.NoValidWindow());
		}

		Result<IWindow> windowResult = ctx.Store.Pick(PickWindowByHandle(windowHandle));
		if (!windowResult.TryGet(out IWindow window))
		{
			return Result.FromError<Unit>(windowResult.Error!);
		}

		Logger.Debug($"Moving window {windowHandle} to workspace {TargetWorkspaceId}");

		// Find the current workspace for the window.
		Result<IWorkspace> oldWorkspaceResult = ctx.Store.Pick(PickWorkspaceByWindow(windowHandle));
		if (!oldWorkspaceResult.TryGet(out IWorkspace oldWorkspace))
		{
			return Result.FromError<Unit>(oldWorkspaceResult.Error!);
		}

		if (oldWorkspace.Id == TargetWorkspaceId)
		{
			Logger.Debug($"Window {windowHandle} is already on workspace {TargetWorkspaceId}");
			return Unit.Result;
		}

		rootSector.MapSector.WindowWorkspaceMap = rootSector.MapSector.WindowWorkspaceMap.SetItem(
			windowHandle,
			TargetWorkspaceId
		);

		ctx.Store.Dispatch(new RemoveWindowFromWorkspaceTransform(oldWorkspace.Id, window) { SkipDoLayout = true });
		ctx.Store.Dispatch(new AddWindowToWorkspaceTransform(TargetWorkspaceId, window) { SkipDoLayout = true });

		// If both workspaces are visible, lay out both.
		if (
			ctx.Store.Pick(PickMonitorByWorkspace(oldWorkspace.Id)).IsSuccessful
			&& ctx.Store.Pick(PickMonitorByWorkspace(targetWorkspace.Id)).IsSuccessful
		)
		{
			ctx.Store.Dispatch(new DoWorkspaceLayoutTransform(TargetWorkspaceId));
			ctx.Store.Dispatch(new DoWorkspaceLayoutTransform(oldWorkspace.Id));
		}
		else if (FocusWindow)
		{
			// The target workspace isn't visible: switch to it (following the window).
			ctx.Store.Dispatch(new ActivateWorkspaceTransform(TargetWorkspaceId));
		}
		else
		{
			// Move the window to the hidden target workspace without switching to it: hide the
			// window and re-lay out the current workspace to fill the gap it leaves behind.
			ctx.NativeManager.HideWindow(windowHandle);

			// Record the deliberate hide so a spurious focus re-assert by the hidden window (e.g.
			// Windows Terminal) doesn't drag the target workspace onto the monitor. See
			// WindowFocusedTransform.
			rootSector.WindowSector.WhimHiddenWindows = rootSector.WindowSector.WhimHiddenWindows.SetItem(
				windowHandle,
				Environment.TickCount
			);

			// Make the moved window the target workspace's last-focused window, so switching to that
			// workspace later focuses it (otherwise nothing has focus there and window commands
			// silently do nothing until the user clicks).
			ctx.Store.Dispatch(new SetLastFocusedWindowTransform(TargetWorkspaceId, windowHandle));

			ctx.Store.Dispatch(new DoWorkspaceLayoutTransform(oldWorkspace.Id));
		}

		if (FocusWindow)
		{
			rootSector.WorkspaceSector.WindowHandleToFocus = window.Handle;
		}

		return Unit.Result;
	}
}
