#ifndef PublishDir
  #error PublishDir must point to a published single-exe directory.
#endif
#ifndef AppVersion
  #error AppVersion must match Directory.Build.props.
#endif
#ifndef OutputDir
  #define OutputDir "bin"
#endif

[Setup]
AppId={{C9E422AD-96D1-4A87-A204-693F06552A98}
AppName=MoeLoaderR
AppVersion={#AppVersion}
AppPublisher=satori-xyz
AppPublisherURL=https://github.com/satori-xyz/MoeLoaderR
AppSupportURL=https://github.com/satori-xyz/MoeLoaderR/issues
DefaultDirName={localappdata}\Programs\MoeLoaderR
DefaultGroupName=MoeLoaderR
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
DisableDirPage=no
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
AlwaysShowDirOnReadyPage=yes
WizardStyle=modern
SetupIconFile=..\MoeLoaderR.Wpf\Assets\moeloaderr.ico
UninstallDisplayIcon={app}\MoeLoaderR.exe
UninstallDisplayName=MoeLoaderR
OutputDir={#OutputDir}
OutputBaseFilename=MoeLoaderR-{#AppVersion}-Setup
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
LicenseFile=..\LICENSE.txt

[Languages]
Name: "zhcn"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
zhcn.CreateDesktopIcon=创建桌面快捷方式
zhcn.LaunchApp=启动 MoeLoaderR
zhcn.NewerInstalled=该目录已经安装了更新版本的 MoeLoaderR。请使用更新的安装包，或选择其他目录。
zhcn.DirectoryHint=请选择安装目录，例如 D:\Apps\MoeLoaderR。请选择当前账户可写入的文件夹，以便软件内更新。
en.CreateDesktopIcon=Create a desktop shortcut
en.LaunchApp=Launch MoeLoaderR
en.NewerInstalled=A newer version of MoeLoaderR is already installed in this folder. Please use a newer installer or choose another folder.
en.DirectoryHint=Choose a writable installation folder, such as D:\Apps\MoeLoaderR, to allow in-app updates.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\MoeLoaderR.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE.txt"; DestDir: "{app}"

[Icons]
Name: "{autoprograms}\MoeLoaderR"; Filename: "{app}\MoeLoaderR.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\MoeLoaderR"; Filename: "{app}\MoeLoaderR.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\MoeLoaderR.exe"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[Code]
procedure InitializeWizard;
begin
  WizardForm.SelectDirLabel.Caption := CustomMessage('DirectoryHint');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExistingVersionText: String;
  ExistingVersion, PackageVersion: Int64;
begin
  Result := '';
  if GetVersionNumbersString(ExpandConstant('{app}\MoeLoaderR.exe'), ExistingVersionText) and
     StrToVersion(ExistingVersionText, ExistingVersion) and
     StrToVersion('{#AppVersion}', PackageVersion) then
    if ComparePackedVersion(ExistingVersion, PackageVersion) > 0 then
      Result := CustomMessage('NewerInstalled');
end;
