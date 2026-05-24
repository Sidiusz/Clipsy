Clipsy icon placeholders.

Replace these PNGs (and clipsy.ico) with the real artwork — file names + sizes must stay the same so the build keeps wiring them in.

Files
  clipsy.ico        Multi-resolution icon used by:
                      * Clipsy.exe (set via <ApplicationIcon> in Clipsy.csproj)
                      * System tray icon (MainWindow.xaml.cs -> TrySetTrayIcon)
                    Should contain 16, 24, 32, 48, 64, 128, 256 px frames.

  clipsy-16.png     16x16   small UI uses
  clipsy-24.png     24x24
  clipsy-32.png     32x32   Settings window titlebar logo
  clipsy-48.png     48x48
  clipsy-64.png     64x64   Tray menu header logo (displayed at 34 DIPs)
  clipsy-128.png    128x128
  clipsy-256.png    256x256

To rebuild clipsy.ico from new PNGs use any ICO packer (e.g. ImageMagick:
  magick clipsy-16.png clipsy-24.png clipsy-32.png clipsy-48.png ^
         clipsy-64.png clipsy-128.png clipsy-256.png clipsy.ico
).
