; ERCTelemetry - Inno Setup script
; Builds the Setup.exe that users download from the project website and that
; installs everything into the user's own folder (%LOCALAPPDATA%\Programs) with
; no UAC: self-contained app (bundled .NET runtime), start-menu + desktop
; shortcut (opt-in), uninstaller. The firewall rule is created by the app itself
; on first start (FirewallService, one-time UAC prompt), not by the installer.
;
; Build:  installer\build-installer.ps1   (runs dotnet publish + ISCC)
; Upload: ERCTelemetry-Setup.exe + .sha256 from installer/release/ to the
;         fixed path on the website server (stable filename, link never changes).
;
; The version comes from the build script via /DAppVersion=x.y.z; the fallback
; below only covers a bare "ISCC installer\ERCTelemetry.iss" compile.

#define AppName "ERCTelemetry"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{61ABF3CA-66F4-4B34-812D-9FD5857AFEE4}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=ERCTelemetry
; User-writable install dir (Discord/VS Code model): updates apply without UAC.
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=release
OutputBaseFilename=ERCTelemetry-Setup-{#AppVersion}
SetupIconFile=..\src\ERCTelemetry.App\erc.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=no
; No elevation: the firewall rule is created by the app on first start instead.
PrivilegesRequired=lowest
; Ask to close a running ERCTelemetry.exe before replacing its files
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
AppMutex=Local\ERCTelemetry.SingleInstance
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
MinVersion=10.0
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
UninstallDisplayName={#AppName}

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[Messages]
WelcomeLabel2=Dies installiert [name/ver] auf Ihrem Computer.%n%nERCTelemetry liest F1 26 Telemetrie (UDP), zeigt Dashboard, In-Game-HUD und OBS-Overlays an und speichert den Sitzungsverlauf.%n%nEs wird empfohlen, alle anderen Anwendungen zu schliessen, bevor Sie fortfahren.

[Files]
; Full self-contained publish output (dotnet runtime bundled) - from installer/stage
Source: "stage\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppName}.exe"
Name: "{group}\{#AppName} deinstallieren"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppName}.exe"; Tasks: desktopicon

[Tasks]
; opt-in desktop shortcut on the task page
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; \
    GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

; No [Run]/[UninstallRun] firewall entries: the app creates the rule on first start
; (FirewallService, one-time UAC prompt) and the uninstaller runs without admin, so it
; cannot remove it either. %LOCALAPPDATA%\ERCTelemetry (settings, history DB) is
; deliberately left untouched by the uninstaller.
