; VPN Nova for Windows installer (Inno Setup 6). Compile on Windows from the folder that
; Scripts/build-windows.sh produces:
;   iscc /DSrc="C:\path\to\VPN Nova" /DVersion=1.1.0 nova.iss
; Installs the app per machine into Program Files. The background service itself is installed by the
; app on first run ("Установить службу", one UAC prompt), exactly like on macOS.

#ifndef Version
  #define Version "dev"
#endif
#ifndef Src
  #define Src "..\..\build\windows\VPN Nova"
#endif

[Setup]
AppId={{6F1F0F3E-9A0E-4B7C-9C1E-2B8F1C7E5A11}
AppName=VPN Nova
AppVersion={#Version}
AppPublisher=VPN Nova
DefaultDirName={autopf}\VPN Nova\App
DefaultGroupName=VPN Nova
DisableProgramGroupPage=yes
OutputBaseFilename=VPN-Nova-{#Version}-setup
SetupIconFile=..\..\build\windows\Nova.ico
UninstallDisplayIcon={app}\Nova.exe
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
PrivilegesRequired=admin
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "{#Src}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{autoprograms}\VPN Nova"; Filename: "{app}\Nova.exe"
Name: "{autodesktop}\VPN Nova"; Filename: "{app}\Nova.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Значок на рабочем столе"; Flags: unchecked

[Run]
Filename: "{app}\Nova.exe"; Description: "Запустить VPN Nova"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
; Removes the service, the core and the logs (the profile in %LOCALAPPDATA%\Nova stays).
Filename: "{app}\Nova.Service.exe"; Parameters: "uninstall"; Flags: runhidden; RunOnceId: "NovaService"

[UninstallDelete]
Type: files; Name: "{userstartup}\VPN Nova.lnk"
