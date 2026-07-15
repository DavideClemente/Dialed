; Inno Setup script for Dialed. Requires Inno Setup 6.3+ (for x64compatible).
; Compile locally:
;   iscc /DAppVersion=1.0.0 "/DPublishDir=<abs path to publish>" installer\Dialed.iss
; PublishDir defaults to the local Release x64 publish output when not supplied.

#define AppName "Dialed"
#define AppPublisher "Davide Clemente"

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef PublishDir
  #define PublishDir "..\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish"
#endif

#ifndef OutputDir
  #define OutputDir "Output"
#endif

[Setup]
AppId={{7B4F0C2E-3A6D-4E51-9B2A-1D9E6C8F0A11}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\Dialed.exe
OutputDir={#OutputDir}
OutputBaseFilename=Dialed-Setup-{#AppVersion}-x64
SetupIconFile=..\Assets\AudioMixer.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Upgrade-while-running: Restart Manager closes a running Dialed (the app
; answers WM_QUERYENDSESSION/WM_ENDSESSION and exits cleanly, bypassing its
; minimize-to-tray dialog) and relaunches it after install (the app registers
; itself via RegisterApplicationRestart with --minimized, so it comes back in
; the tray). Both are Inno defaults, but this behavior is load-bearing here.
CloseApplications=yes
RestartApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\Dialed.exe"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Dialed.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Dialed.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
