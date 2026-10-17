; Aurora installer script for Inno Setup 6
;
; Build:
;   1. dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false \
;        -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true -p:PublishTrimmed=false
;   2. ISCC.exe Aurora.iss        (produces Output\Aurora-1.5.0-Setup.exe)
;
; The published folder is self-contained: it carries .NET and the Windows App SDK runtime, so the
; machine being installed on needs nothing pre-installed, and there is no package to sign.
;
; Two notes before shipping this:
;
;   * The file-association task below is ticked by default on a first install. Aurora used to open
;     files only through a WinRT file activation, which an unpackaged build never receives, so an
;     association registered by this installer would have started the app without opening the file.
;     The entry point now reads its command line as well, and both routes are supported: a packaged
;     build is activated with its files, an unpackaged one is handed them as arguments.
;   * No code signing is configured. Expect a SmartScreen warning on first run; that is a signing
;     matter, not an installer defect.

#define AppName "Aurora"
#define AppVersion "1.5.1"
#define AppExeName "Aurora.exe"
#define SourceDir "..\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish"

[Setup]
AppId={{43B29042-BED9-49C4-B07F-3E79FB388569}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Aurora
DefaultDirName={autopf}\Aurora
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=Aurora-1.5.1-Setup
SetupIconFile={#SourceDir}\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Aurora targets Windows 11 24H2 (build 26100) and later. The build refuses to start below that.
MinVersion=10.0.26100
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Associations are written to HKLM, and {autopf} is Program Files, so this needs elevation.
PrivilegesRequired=admin
; Tells the shell that associations changed, so Explorer picks them up without a restart.
ChangesAssociations=yes
; One language: the wizard does not ask which one to use.
ShowLanguageDialog=no

[Languages]
; Chinese only, and no language dialog: the wizard is meant to be read in one language from beginning
; to end. Inno Setup does not ship a Chinese translation, so ChineseSimplified.isl comes from the
; project's own repository (Files\Languages\ChineseSimplified.isl) and lives in the compiler's
; Languages folder. The English messages stay referenced by nothing; the file is kept next to this
; script if a second language is ever wanted back.
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "associate"; Description: "关联音频文件（mp3、flac、wav 等 13 种）"; GroupDescription: "文件关联"; Flags: checkedonce

[Files]
; Everything the publish produced, including the Assets folder the startup reveal and the window
; icon are read from, and the libvlc folder playback needs.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "运行 {#AppName}"; Flags: nowait postinstall skipifsilent

[Registry]
; The classic per-extension association: the extension points at a ProgId, and the ProgId carries
; the icon and the open command. uninsdeletevalue / uninsdeletekey remove them again on uninstall.
; Every line is bound to the associate task, so nothing is written unless it was chosen.

; "Open with" entry, so Aurora can be picked for a file even before it is the default.
Root: HKLM; Subkey: "Software\Classes\Applications\{#AppExeName}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Applications\{#AppExeName}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""; Flags: uninsdeletekey; Tasks: associate

; ---- .mp3 ----
Root: HKLM; Subkey: "Software\Classes\.mp3"; ValueType: string; ValueName: ""; ValueData: "Aurora.mp3"; Flags: uninsdeletevalue; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.mp3"; ValueType: string; ValueName: ""; ValueData: "MP3 音频文件"; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.mp3\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.mp3\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".mp3"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKLM; Subkey: "Software\Aurora\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mp3"; ValueData: "Aurora.mp3"; Flags: uninsdeletevalue; Tasks: associate

; ---- .flac ----
Root: HKLM; Subkey: "Software\Classes\.flac"; ValueType: string; ValueName: ""; ValueData: "Aurora.flac"; Flags: uninsdeletevalue; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.flac"; ValueType: string; ValueName: ""; ValueData: "FLAC 音频文件"; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.flac\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.flac\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".flac"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKLM; Subkey: "Software\Aurora\Capabilities\FileAssociations"; ValueType: string; ValueName: ".flac"; ValueData: "Aurora.flac"; Flags: uninsdeletevalue; Tasks: associate

; ---- .wav ----
Root: HKLM; Subkey: "Software\Classes\.wav"; ValueType: string; ValueName: ""; ValueData: "Aurora.wav"; Flags: uninsdeletevalue; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.wav"; ValueType: string; ValueName: ""; ValueData: "WAV 音频文件"; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.wav\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.wav\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".wav"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKLM; Subkey: "Software\Aurora\Capabilities\FileAssociations"; ValueType: string; ValueName: ".wav"; ValueData: "Aurora.wav"; Flags: uninsdeletevalue; Tasks: associate

; ---- .m4a ----
Root: HKLM; Subkey: "Software\Classes\.m4a"; ValueType: string; ValueName: ""; ValueData: "Aurora.m4a"; Flags: uninsdeletevalue; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.m4a"; ValueType: string; ValueName: ""; ValueData: "M4A 音频文件"; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.m4a\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.m4a\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".m4a"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKLM; Subkey: "Software\Aurora\Capabilities\FileAssociations"; ValueType: string; ValueName: ".m4a"; ValueData: "Aurora.m4a"; Flags: uninsdeletevalue; Tasks: associate

; ---- .aac ----
Root: HKLM; Subkey: "Software\Classes\.aac"; ValueType: string; ValueName: ""; ValueData: "Aurora.aac"; Flags: uninsdeletevalue; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.aac"; ValueType: string; ValueName: ""; ValueData: "AAC 音频文件"; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.aac\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.aac\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".aac"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKLM; Subkey: "Software\Aurora\Capabilities\FileAssociations"; ValueType: string; ValueName: ".aac"; ValueData: "Aurora.aac"; Flags: uninsdeletevalue; Tasks: associate

; ---- .ogg ----
Root: HKLM; Subkey: "Software\Classes\.ogg"; ValueType: string; ValueName: ""; ValueData: "Aurora.ogg"; Flags: uninsdeletevalue; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.ogg"; ValueType: string; ValueName: ""; ValueData: "OGG 音频文件"; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.ogg\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.ogg\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".ogg"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKLM; Subkey: "Software\Aurora\Capabilities\FileAssociations"; ValueType: string; ValueName: ".ogg"; ValueData: "Aurora.ogg"; Flags: uninsdeletevalue; Tasks: associate

; ---- .oga ----
Root: HKLM; Subkey: "Software\Classes\.oga"; ValueType: string; ValueName: ""; ValueData: "Aurora.oga"; Flags: uninsdeletevalue; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.oga"; ValueType: string; ValueName: ""; ValueData: "OGA 音频文件"; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.oga\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.oga\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".oga"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKLM; Subkey: "Software\Aurora\Capabilities\FileAssociations"; ValueType: string; ValueName: ".oga"; ValueData: "Aurora.oga"; Flags: uninsdeletevalue; Tasks: associate

; ---- .opus ----
Root: HKLM; Subkey: "Software\Classes\.opus"; ValueType: string; ValueName: ""; ValueData: "Aurora.opus"; Flags: uninsdeletevalue; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.opus"; ValueType: string; ValueName: ""; ValueData: "Opus 音频文件"; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.opus\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.opus\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".opus"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKLM; Subkey: "Software\Aurora\Capabilities\FileAssociations"; ValueType: string; ValueName: ".opus"; ValueData: "Aurora.opus"; Flags: uninsdeletevalue; Tasks: associate

; ---- .wma ----
Root: HKLM; Subkey: "Software\Classes\.wma"; ValueType: string; ValueName: ""; ValueData: "Aurora.wma"; Flags: uninsdeletevalue; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.wma"; ValueType: string; ValueName: ""; ValueData: "WMA 音频文件"; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.wma\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.wma\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".wma"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKLM; Subkey: "Software\Aurora\Capabilities\FileAssociations"; ValueType: string; ValueName: ".wma"; ValueData: "Aurora.wma"; Flags: uninsdeletevalue; Tasks: associate

; ---- .aiff ----
Root: HKLM; Subkey: "Software\Classes\.aiff"; ValueType: string; ValueName: ""; ValueData: "Aurora.aiff"; Flags: uninsdeletevalue; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.aiff"; ValueType: string; ValueName: ""; ValueData: "AIFF 音频文件"; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.aiff\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.aiff\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".aiff"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKLM; Subkey: "Software\Aurora\Capabilities\FileAssociations"; ValueType: string; ValueName: ".aiff"; ValueData: "Aurora.aiff"; Flags: uninsdeletevalue; Tasks: associate

; ---- .aif ----
Root: HKLM; Subkey: "Software\Classes\.aif"; ValueType: string; ValueName: ""; ValueData: "Aurora.aif"; Flags: uninsdeletevalue; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.aif"; ValueType: string; ValueName: ""; ValueData: "AIF 音频文件"; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.aif\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.aif\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".aif"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKLM; Subkey: "Software\Aurora\Capabilities\FileAssociations"; ValueType: string; ValueName: ".aif"; ValueData: "Aurora.aif"; Flags: uninsdeletevalue; Tasks: associate

; ---- .ape ----
Root: HKLM; Subkey: "Software\Classes\.ape"; ValueType: string; ValueName: ""; ValueData: "Aurora.ape"; Flags: uninsdeletevalue; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.ape"; ValueType: string; ValueName: ""; ValueData: "APE 音频文件"; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.ape\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.ape\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".ape"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKLM; Subkey: "Software\Aurora\Capabilities\FileAssociations"; ValueType: string; ValueName: ".ape"; ValueData: "Aurora.ape"; Flags: uninsdeletevalue; Tasks: associate

; ---- .mka ----
Root: HKLM; Subkey: "Software\Classes\.mka"; ValueType: string; ValueName: ""; ValueData: "Aurora.mka"; Flags: uninsdeletevalue; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.mka"; ValueType: string; ValueName: ""; ValueData: "MKA 音频文件"; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.mka\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Aurora.mka\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".mka"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKLM; Subkey: "Software\Aurora\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mka"; ValueData: "Aurora.mka"; Flags: uninsdeletevalue; Tasks: associate

; Windows 11 only offers an app in Settings > Default apps when it registers its capabilities
; here, so this is what makes Aurora selectable as a default rather than merely present.
Root: HKLM; Subkey: "Software\Aurora\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\Aurora\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Aurora 本地音频播放器"; Flags: uninsdeletekey; Tasks: associate
Root: HKLM; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "{#AppName}"; ValueData: "Software\Aurora\Capabilities"; Flags: uninsdeletevalue; Tasks: associate

[Code]
{ The environment check, run before the wizard puts up its first page.
  It only decides whether installing is allowed to start; nothing here touches what gets installed,
  which associations are written, or which shortcuts are created. }

const
  { Windows 11 24H2. The same floor the application itself refuses to start below, and the same one
    Aurora.csproj and its manifest declare, so a machine that passes here is one the app will run on. }
  RequiredBuild = 26100;

  { Room for the payload plus some slack. The published folder is about 580 MB, and Inno needs somewhere
    to unpack to while it works. }
  RequiredFreeMB = 1024;

function InitializeSetup(): Boolean;
var
  Version: TWindowsVersion;
  FreeMB: Int64;
  TotalMB: Int64;
  FreeText: string;
begin
  Result := False;

  { Architecture first: everything below is meaningless on a 32-bit system, which could not run the
    x64 build at all. }
  if not IsWin64 then
  begin
    MsgBox('Aurora 需要 64 位版本的 Windows。' + #13#10 + #13#10 +
           '检测到当前系统为 32 位。Aurora 的播放核心与运行时只有 64 位版本，无法在 32 位系统上运行，' +
           '请改用 64 位系统后再试。',
           mbCriticalError, MB_OK);
    Exit;
  end;

  { Then the build. Major 10 covers Windows 10 and 11; the build number is what separates them, and
    24H2 is where the window features Aurora relies on exist. }
  GetWindowsVersionEx(Version);

  if (Version.Major < 10) or ((Version.Major = 10) and (Version.Build < RequiredBuild)) then
  begin
    MsgBox('Aurora 需要 Windows 11 24H2（内部版本 26100）或更高版本。' + #13#10 + #13#10 +
           '您当前运行的是：' + GetWindowsVersionString + #13#10 +
           '检测到的内部版本号：' + IntToStr(Version.Build) + #13#10 + #13#10 +
           'Aurora 的窗口效果与音频运行时依赖 24H2 及以上版本提供的能力，' +
           '在更早的系统上无法正常启动。请升级系统后再试。',
           mbCriticalError, MB_OK);
    Exit;
  end;

  { Finally room to install. The drive is taken from the folder the user would be installing into, so
    this reflects the real target rather than whatever drive the installer itself is running from. }
  if not GetSpaceOnDisk64(ExpandConstant('{autopf}'), FreeMB, TotalMB) then
  begin
    { Not being able to measure is not a reason to refuse: let the install proceed and let Windows be
      the one to object if it really runs out of room. }
    Result := True;
    Exit;
  end;

  if FreeMB < RequiredFreeMB then
  begin
    if FreeMB >= 1024 then
      FreeText := IntToStr(FreeMB div 1024) + ' GB'
    else
      FreeText := IntToStr(FreeMB) + ' MB';

    MsgBox('Aurora 需要至少 1 GB 的可用磁盘空间。' + #13#10 + #13#10 +
           '安装目标所在磁盘当前可用：' + FreeText + #13#10 +
           '（Aurora 完整安装约需 1.6 GB，其中包含 .NET 与音频运行时。）' + #13#10 + #13#10 +
           '请清理磁盘空间后再试。',
           mbCriticalError, MB_OK);
    Exit;
  end;

  Result := True;
end;
