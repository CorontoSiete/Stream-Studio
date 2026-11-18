using System.Windows.Interop;

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> OverlayPointerLeaveTests =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")) ? [] :
        [("native chat overlay clears hover when the pointer leaves the main window", OverlayPointerLeavesMainWindowAsync)];

    private static Task OverlayPointerLeavesMainWindowAsync() => WithResponsiveWindowAsync(withVideo: true, async (window, _) =>
    {
        Assert.True(NativeWindowTest.TryGetCursorPosition(out var originalCursor));
        var root = CreateTempTestDirectory();
        try
        {
            const int sourceWidth = 1920, sourceHeight = 1080;
            const int panelWidth = 360, panelHeight = 240, panelTop = 120;
            var positionPath = Path.Combine(root, "position");
            File.WriteAllText(positionPath, $"{sourceWidth - panelWidth} {panelTop} 0");
            var media = Path.Combine(root, "video.bmp");
            using (var bitmap = new System.Drawing.Bitmap(sourceWidth, sourceHeight))
            {
                using var graphics = System.Drawing.Graphics.FromImage(bitmap);
                graphics.Clear(System.Drawing.Color.FromArgb(65, 70, 78));
                bitmap.Save(media);
            }

            window.Topmost = true;
            ResizeResponsiveWindow(window, 800, 650);
            var windowHandle = new WindowInteropHelper(window).Handle;
            await NativeWindowTest.RequireForegroundAsync(windowHandle, TimeSpan.FromSeconds(2),
                "Chat hover requires an interactive desktop");
            var surface = FindVisualDescendants<VideoSurface>(window).Single();
            var logger = new MemoryLogger();
            var overlayDirectory = VlcOverlayBundledResourceExtractor.TryExtract(logger, root);
            Assert.True(overlayDirectory is not null, "The bundled overlay must extract for this test.");
            var settings = new ChatSettings { VlcOverlayDirectory = overlayDirectory! };
            using var engine = await LibVlcPlaybackEngine.CreateAsync(
                Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!, settings, logger,
                enableNativeOverlay: true, nativeOverlayPositionStatePath: positionPath, CancellationToken.None,
                VideoRendererMode.Gdi, nativeOverlayAppDataDirectory: root);
            Assert.True(engine.UsesNativeOverlay, "The test must use the bundled native overlay.");
            engine.SetVideoHandle(surface.Handle);
            await engine.PlayAsync(new Uri(media), 0, PlaybackAudioState.HardMuted);
            await TestWait.UntilAsync(() => engine.TryGetVideoSize(out var width, out var height) &&
                width == sourceWidth && height == sourceHeight, TimeSpan.FromSeconds(5));
            PumpResponsiveLayout(window);

            var video = NativeWindowTest.GetWindowBounds(surface.Handle);
            var owner = NativeWindowTest.GetWindowBounds(windowHandle);
            Assert.Equal(owner.Right, video.Right);
            var scale = video.Width / (double)sourceWidth;
            var videoTop = (video.Height - sourceHeight * scale) / 2;
            int X(int distanceFromRight) => video.Width - (int)Math.Round(distanceFromRight * scale);
            int Y(int sourceY) => (int)Math.Round(videoTop + sourceY * scale);
            var hoverX = video.Left + X(20);
            var hoverY = video.Top + Y(panelTop + panelHeight - 18);
            var outsideX = owner.Right + 16;
            Assert.True(NativeWindowTest.IsRootWindowAtPoint(windowHandle, hoverX, hoverY));
            Assert.True(!NativeWindowTest.IsRootWindowAtPoint(windowHandle, outsideX, hoverY));
            NativeWindowTest.SetCursorPosition(outsideX, hoverY);

            var frame = NativeOverlayProtocolCodec.CreateFrameMessage(panelWidth, panelHeight);
            for (var pixel = NativeOverlayProtocolCodec.HeaderSize; pixel < frame.Length; pixel += 4)
            {
                frame[pixel] = 180;
                frame[pixel + 2] = 180;
                frame[pixel + 3] = 255;
            }
            await using (var pipe = new NamedPipeClientStream(".", engine.NativeOverlayPipeName!, PipeDirection.Out, PipeOptions.Asynchronous))
            {
                await pipe.ConnectAsync(3000);
                await pipe.WriteAsync(frame);
            }

            bool ChatControlsMatch(bool visible, int panelOffset = 0)
            {
                using var capture = CaptureReplayVlcSurface(surface);
                var panel = capture.GetPixel(X(35), Y(panelTop + 65) + panelOffset);
                var button = capture.GetPixel(X(12), Y(panelTop + 12) + panelOffset);
                return panel.R > 130 && panel.G < 40 && panel.B > 130 &&
                    (visible ? button.G > 90 : button.R > 130 && button.G < 40 && button.B > 130);
            }

            await TestWait.UntilAsync(() => ChatControlsMatch(visible: false), TimeSpan.FromSeconds(2),
                "The overlay frame should be visible with its controls hidden.");
            for (var pass = 0; pass < 2; pass++)
            {
                NativeWindowTest.SetCursorPosition(hoverX, hoverY);
                await TestWait.UntilAsync(() => ChatControlsMatch(visible: true), TimeSpan.FromSeconds(2),
                    "Hovering the edge of chat should reveal the native controls.");
                NativeWindowTest.SetCursorPosition(outsideX, hoverY);
                Assert.True(NativeWindowTest.TryGetCursorPosition(out var current) && current.X == outsideX);
                await TestWait.UntilAsync(() => ChatControlsMatch(visible: false), TimeSpan.FromSeconds(2),
                    "The controls should disappear after one move straight from chat to outside the app.");
            }

            var moveY = video.Top + Y(panelTop + 16);
            var dragDistance = Y(panelTop + panelHeight) - Y(panelTop);
            NativeWindowTest.SetCursorPosition(video.Left + X(panelWidth - 24), moveY);
            await TestWait.UntilAsync(() => ChatControlsMatch(visible: true), TimeSpan.FromSeconds(2),
                "The MOVE handle should be available before starting the drag.");
            ReplayVlcMouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero);
            try
            {
                NativeWindowTest.SetCursorPosition(outsideX, moveY + dragDistance);
                await TestWait.UntilAsync(() => ChatControlsMatch(visible: true, dragDistance), TimeSpan.FromSeconds(2),
                    "A captured MOVE drag should still move the panel and keep its controls visible outside the app.");
            }
            finally
            {
                ReplayVlcMouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
            }

            await TestWait.UntilAsync(() => ChatControlsMatch(visible: false, dragDistance), TimeSpan.FromSeconds(2),
                "Releasing the drag outside the app should hide the controls at the new panel position.");
        }
        finally
        {
            NativeWindowTest.SetCursorPosition(originalCursor.X, originalCursor.Y);
            DeleteTempTestDirectory(root);
        }
    });
}
