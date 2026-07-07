namespace Whim;

internal record WindowFocusedTransform(IWindow? Window) : Transform()
{
	internal override Result<Unit> Execute(
		IContext ctx,
		IInternalContext internalCtx,
		MutableRootSector mutableRootSector
	)
	{
		SetActiveMonitor(ctx, internalCtx, mutableRootSector);
		UpdateMapSector(ctx, internalCtx, mutableRootSector, Window);

		mutableRootSector.WindowSector.QueueEvent(new WindowFocusedEventArgs() { Window = Window });

		return Unit.Result;
	}

	/// <summary>
	/// Set the active monitor.
	/// </summary>
	/// <param name="ctx"></param>
	/// <param name="internalCtx"></param>
	/// <param name="root"></param>
	private void SetActiveMonitor(IContext ctx, IInternalContext internalCtx, MutableRootSector root)
	{
		MonitorSector monitorSector = root.MonitorSector;

		// If we know the window, use what the map sector knows instead of Windows.
		if (Window is not null && ctx.Store.Pick(PickMonitorByWindow(Window.Handle)).TryGet(out IMonitor monitor))
		{
			Logger.Debug($"Setting active monitor to {monitor}");
			monitorSector.ActiveMonitorHandle = monitor.Handle;
			monitorSector.LastWhimActiveMonitorHandle = monitor.Handle;
			return;
		}

		// We don't know the window, so get the foreground window.
		HWND hwnd = Window?.Handle ?? internalCtx.CoreNativeManager.GetForegroundWindow();
		Logger.Debug($"Focusing hwnd {hwnd}");

		if (hwnd.IsNull)
		{
			Logger.Debug($"Hwnd is desktop window, ignoring");
			return;
		}

		HMONITOR monitorHandle = internalCtx.CoreNativeManager.MonitorFromWindow(
			hwnd,
			MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST
		);

		foreach (IMonitor currentMonitor in monitorSector.Monitors)
		{
			if (!currentMonitor.Handle.Equals(monitorHandle))
			{
				continue;
			}

			Logger.Debug($"Setting active monitor to {currentMonitor}");
			monitorSector.ActiveMonitorHandle = currentMonitor.Handle;

			// The window isn't tracked by Whim, so don't update LastWhimActiveMonitorHandle.
			if (Window is not null)
			{
				monitorSector.LastWhimActiveMonitorHandle = currentMonitor.Handle;
			}
			break;
		}
	}

	/// <summary>
	/// How long after Whim deliberately hides a window its focus and minimize-start events are
	/// treated as spurious. Nothing legitimate can focus or minimize a hidden window this quickly -
	/// hidden windows have no taskbar button and do not appear in Alt+Tab. See also
	/// <see cref="WindowMinimizeStartedTransform"/>.
	/// </summary>
	internal const int WhimHiddenGracePeriodMs = 1000;

	private static void UpdateMapSector(
		IContext ctx,
		IInternalContext internalCtx,
		MutableRootSector rootSector,
		IWindow? window
	)
	{
		// Only activate the workspace if the window is in a workspace, and the workspace isn't currently
		// active.
		if (window is null)
		{
			return;
		}

		if (!ctx.Store.Pick(PickWorkspaceByWindow(window.Handle)).TryGet(out IWorkspace? workspaceForWindow))
		{
			return;
		}

		ctx.Store.Dispatch(new SetLastFocusedWindowTransform(workspaceForWindow.Id, window.Handle));

		if (ctx.Store.Pick(PickMonitorByWorkspace(workspaceForWindow.Id)).IsSuccessful)
		{
			return;
		}

		// Some apps - notably Windows Terminal, which hosts all its windows in a single process -
		// re-show themselves and re-assert foreground immediately after Whim hides them during a
		// workspace switch. Treating that as intent would activate the just-deactivated workspace and
		// undo the switch. If Whim deliberately hid this window a moment ago, re-hide it instead: it
		// must not be left genuinely visible (e.g. showing up in Alt+Tab from another workspace).
		//
		// Re-hiding was previously found to sometimes make Windows Terminal report a genuine
		// EVENT_SYSTEM_MINIMIZESTART for the window immediately afterwards. That's no longer a
		// problem: WindowMinimizeStartedTransform now recognizes and ignores a minimize-start for a
		// window Whim just hid, so it can't get stuck needing a manual Alt+Tab to recover.
		if (
			rootSector.WindowSector.WhimHiddenWindows.TryGetValue(window.Handle, out int hiddenAtTickCount)
			&& Environment.TickCount - hiddenAtTickCount < WhimHiddenGracePeriodMs
		)
		{
			Logger.Debug($"Window {window} was just hidden by Whim; re-hiding instead of activating its workspace");
			ctx.NativeManager.HideWindow(window.Handle);
			return;
		}

		// Only re-activate the window's workspace if the window is actually visible. A user cannot
		// deliberately focus a window that isn't on screen; genuinely summoned windows (taskbar
		// clicks, URL handlers, etc.) are visible by the time this runs.
		if (!internalCtx.CoreNativeManager.IsWindowVisible(window.Handle))
		{
			Logger.Debug($"Window {window} is not visible; not activating workspace {workspaceForWindow.Id}");
			return;
		}

		ctx.Store.Dispatch(new ActivateWorkspaceTransform(workspaceForWindow.Id));
	}
}
