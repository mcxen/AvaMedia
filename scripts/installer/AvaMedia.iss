#ifndef AppVersion
  #define AppVersion "1.0.5"
#endif
#ifndef AppChineseName
  #error AppChineseName is required
#endif
#ifndef AppDisplayName
  #error AppDisplayName is required
#endif
#ifndef PublishDir
  #error PublishDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif

[Setup]
AppId={{957F8776-D499-4687-B2CE-36041338AD68}
AppName={#AppDisplayName}
AppVersion={#AppVersion}
AppPublisher=AvaMedia contributors
AppPublisherURL=https://github.com/mcxen/AvaMedia
AppSupportURL=https://github.com/mcxen/AvaMedia/issues
AppUpdatesURL=https://github.com/mcxen/AvaMedia/releases
DefaultDirName={localappdata}\Programs\AvaMedia
DefaultGroupName={#AppChineseName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile={#PublishDir}\LICENSE
SetupIconFile=..\..\src\AvaMedia.Desktop\Assets\AppIcon\v2\app.ico
UninstallDisplayIcon={app}\AvaMedia.Desktop.exe
OutputDir={#OutputDir}
OutputBaseFilename=AvaMedia-{#AppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
Name: "playerdesktopicon"; Description: "Create a Tianchi Player desktop shortcut"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppChineseName}"; Filename: "{app}\AvaMedia.Desktop.exe"
Name: "{group}\天池播放器"; Filename: "{app}\AvaMedia.Desktop.exe"; Parameters: "--play"
Name: "{group}\卸载 {#AppChineseName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppChineseName}"; Filename: "{app}\AvaMedia.Desktop.exe"; Tasks: desktopicon
Name: "{autodesktop}\天池播放器"; Filename: "{app}\AvaMedia.Desktop.exe"; Parameters: "--play"; Tasks: playerdesktopicon

[Run]
Filename: "{app}\AvaMedia.Desktop.exe"; Parameters: "--register-player"; Flags: runhidden waituntilterminated
Filename: "{app}\AvaMedia.Desktop.exe"; Description: "启动 {#AppChineseName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\AvaMedia.Desktop.exe"; Parameters: "--unregister-player"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterTianchiPlayer"
