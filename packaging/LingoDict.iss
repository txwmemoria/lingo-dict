; LingoDict 安装脚本（Inno Setup 6）
;
; 单窗口自定义界面：默认的多页向导整个藏起来，内容全部落在一张背景图上，
; 底部是进度条。见文件末尾的 [Code]。
;
; 装到 %LOCALAPPDATA%\LingoDict：装的是个人词典，PrivilegesRequired=lowest
; 全程不弹 UAC，卸载也不需要管理员。
;
; 由 scripts/pack-installer.ps1 调用，AppVersion / SourceDir / OutputDir
; 都从命令行 /D 传入，不在这里写死。

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#define AppName "LingoDict"
#define AppExe  "LingoDict.exe"

[Setup]
; AppId 是产品的永久标识，升级靠它认出旧版本——任何时候都不能改，
; 改了之后新版会被当成另一个产品，和旧版并排装两份。
AppId={{8C3F1A62-5D74-4E38-9B21-6A0C7E4F19D3}
AppName=LingoDict
AppVersion={#AppVersion}
AppVerName=LingoDict 离线英语词典 {#AppVersion}
AppPublisher=LingoDict
VersionInfoVersion={#AppVersion}

DefaultDirName={localappdata}\{#AppName}
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

OutputDir={#OutputDir}
OutputBaseFilename=LingoDict-{#AppVersion}-win-x64-Setup
SetupIconFile={#RepoDir}\src\LingoDict.App\Assets\lingodict.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName=LingoDict 离线英语词典

Compression=lzma2/max
SolidCompression=yes
LZMANumBlockThreads=4

; 界面由 [Code] 全盘接管，这里只关掉用不上的默认页
WizardStyle=modern
DisableProgramGroupPage=yes
DisableReadyPage=yes
ShowLanguageDialog=no

[Languages]
Name: "chinese"; MessagesFile: "{#RepoDir}\packaging\ChineseSimplified.isl"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "离线英语词典"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Check: WantDesktopIcon

[Run]
Filename: "{app}\{#AppExe}"; Flags: nowait postinstall skipifsilent; Check: WantRun

; 刻意不写 [UninstallDelete]：收藏、笔记、学习进度在
; %APPDATA%\LingoDict\userdata.db，不是安装目录里的东西，卸载不该动它。

[Code]
const
  FormW = 580;
  FormH = 380;

  { TColor 是 BGR，跟 Themes/Tokens.axaml 里的 #RRGGBB 字节序相反 }
  ColInk      = $3A2016;  { BrandInk         #16203A }
  ColTitle    = $FFFFFF;  { BrandTitle       #FFFFFF }
  ColSubtitle = $CEB2A2;  { BrandSubtitle    #A2B2CE }
  ColAccent   = $F06C2B;  { SemiColorPrimary #2B6CF0 }
  ColField    = $4C3125;  { SearchField      #25314C }
  ColFieldEdge= $6A493A;  { SearchFieldBorder #3A496A }

var
  Bg: TBitmapImage;
  DirLabel: TBitmapImage;
  DirPath: String;
  BrowseButton: TBitmapImage;
  DesktopCheck, RunCheck: TBitmapImage;
  DesktopOn, RunOn: Boolean;
  GoButton: TBitmapImage;
  EnterTrigger: TNewButton;
  Status: TNewStaticText;
  BarBack, BarFill: TPanel;
  CW, CH: Integer;      { 客户区实际尺寸，见 InitializeWizard }
  Installing: Boolean;

{ ---------- 给 [Icons] / [Run] 用的 Check ---------- }

function WantDesktopIcon: Boolean;
begin
  Result := DesktopOn;
end;

function WantRun: Boolean;
begin
  Result := RunOn;
end;

{ ---------- 背景 ---------- }

{ 运行时按客户区实际尺寸绘制，而不是拉伸一张固定大小的位图：
  高 DPI 下拉伸会糊，直接画则任何缩放都清晰。
  静态文字也画进位图，省掉一堆要处理透明背景的 Label。 }
{ 渐变 #16203A → #0D131E 上任意一行的颜色。按钮位图是独立的一张，
  圆角之外要露出背景，只能把同一条渐变按绝对位置再算一遍。 }
function GradientAt(AbsY: Integer): Integer;
var
  T: Extended;
begin
  T := AbsY / (CH - 1);
  Result := Round($16 + ($0D - $16) * T)
       or (Round($20 + ($13 - $20) * T) shl 8)
       or (Round($3A + ($1E - $3A) * T) shl 16);
end;

procedure PaintBackground;
var
  C: TCanvas;
  Y: Integer;
begin
  Bg.Bitmap.Width := CW;
  Bg.Bitmap.Height := CH;
  C := Bg.Bitmap.Canvas;

  for Y := 0 to CH - 1 do
  begin
    C.Pen.Color := GradientAt(Y);
    C.MoveTo(0, Y);
    C.LineTo(CW, Y);
  end;

  C.Pen.Color := ColAccent;
  C.Brush.Color := ColAccent;
  C.Rectangle(ScaleX(40), ScaleY(96), ScaleX(76), ScaleY(99));

  C.Brush.Style := bsClear;

  C.Font.Name := 'Segoe UI';
  C.Font.Style := [fsBold];
  C.Font.Size := 28;
  C.Font.Color := ColTitle;
  C.TextOut(ScaleX(38), ScaleY(40), 'LINGO');

  C.Font.Name := 'Microsoft YaHei';
  C.Font.Style := [];
  C.Font.Color := ColSubtitle;

  C.Font.Size := 10;
  C.TextOut(ScaleX(40), ScaleY(110), '离线英语词典  ·  {#AppVersion}');

  { 「安装位置」是选项阶段的标签，开装之后连同输入框一起消失。
    文字画在位图里没法单独擦，只能整张重绘。 }
  if not Installing then
  begin
    C.Font.Size := 9;
    C.TextOut(ScaleX(40), ScaleY(166), '安装位置');
  end;

  C.Font.Size := 8;
  C.TextOut(ScaleX(40), CH - ScaleY(42), '词书数据来自 kajweb 单词本源项目 · 仅供个人学习使用');
  C.TextOut(ScaleX(40), CH - ScaleY(26), '所需空间约 230 MB · 卸载不会删除你的收藏和笔记');

  Bg.Invalidate;
end;

{ ---------- 扁平按钮 ---------- }

{ 原生 TNewButton 由系统主题画，在深色背景上是一块白，设 Color 也没用；
  TPanel 能上色但它的 OnClick 在 Inno 的 Pascal 里不触发。只能自绘。 }
procedure PaintFlatButton(Img: TBitmapImage; Caption: String;
                          Fill, TextColor: Integer; FontSize: Integer);
var
  C: TCanvas;
  W, H, Y: Integer;
begin
  W := Img.Width;
  H := Img.Height;
  Img.Bitmap.Width := W;
  Img.Bitmap.Height := H;
  C := Img.Bitmap.Canvas;

  { 先铺背景渐变，圆角切掉的四角才不会是一块突兀的纯色 }
  for Y := 0 to H - 1 do
  begin
    C.Pen.Color := GradientAt(Img.Top + Y);
    C.MoveTo(0, Y);
    C.LineTo(W, Y);
  end;

  C.Pen.Color := Fill;
  C.Brush.Color := Fill;
  C.RoundRect(0, 0, W, H, ScaleX(6), ScaleX(6));

  C.Brush.Style := bsClear;
  C.Font.Name := 'Microsoft YaHei';
  C.Font.Size := FontSize;
  C.Font.Color := TextColor;
  C.TextOut((W - C.TextWidth(Caption)) div 2, (H - C.TextHeight(Caption)) div 2, Caption);
  Img.Invalidate;
end;

procedure PaintGoButton(Caption: String);
begin
  PaintFlatButton(GoButton, Caption, ColAccent, ColTitle, 11);
end;

{ ---------- 安装位置 ---------- }

{ 路径也画出来而不是用 TNewEdit：编辑框是窗体上唯一能聚焦的控件，
  一拿到焦点 Windows 就把整条路径反白，看着像报错，而且清选区的时机
  怎么都赶不上 Inno 设焦点。改成只读展示 + 点击浏览，顺带把原生控件清零。 }
procedure PaintDirLabel;
var
  C: TCanvas;
  W, H, Y, Pad: Integer;
  Text: String;
begin
  W := DirLabel.Width;
  H := DirLabel.Height;
  DirLabel.Bitmap.Width := W;
  DirLabel.Bitmap.Height := H;
  C := DirLabel.Bitmap.Canvas;

  for Y := 0 to H - 1 do
  begin
    C.Pen.Color := GradientAt(DirLabel.Top + Y);
    C.MoveTo(0, Y);
    C.LineTo(W, Y);
  end;

  C.Pen.Color := ColField;
  C.Brush.Color := ColField;
  C.RoundRect(0, 0, W, H, ScaleX(6), ScaleX(6));

  C.Brush.Style := bsClear;
  C.Font.Name := 'Microsoft YaHei';
  C.Font.Size := 9;
  C.Font.Color := ColTitle;

  Pad := ScaleX(12);
  Text := DirPath;
  { 路径过长就从左边省略，末尾的目录名比盘符有用 }
  while (Length(Text) > 4) and (C.TextWidth('…' + Text) > W - Pad * 2) do
    Text := Copy(Text, 2, Length(Text) - 1);
  if Text <> DirPath then
    Text := '…' + Text;

  C.TextOut(Pad, (H - C.TextHeight(Text)) div 2, Text);
  DirLabel.Invalidate;
end;

procedure OnBrowse(Sender: TObject);
var
  Dir: String;
begin
  Dir := DirPath;
  if BrowseForFolder('选择安装位置', Dir, True) then
  begin
    DirPath := Dir;
    PaintDirLabel;
  end;
end;

procedure ShowOptions(Visible: Boolean);
begin
  DirLabel.Visible := Visible;
  BrowseButton.Visible := Visible;
  DesktopCheck.Visible := Visible;
  RunCheck.Visible := Visible;
end;

procedure OnGo(Sender: TObject);
begin
  { 装完之后这颗按钮变成「开始使用」，点它就走 Inno 的收尾流程 }
  if WizardForm.CurPageID = wpFinished then
  begin
    WizardForm.NextButton.OnClick(WizardForm.NextButton);
    Exit;
  end;

  WizardForm.DirEdit.Text := DirPath;
  GoButton.Visible := False;
  ShowOptions(False);
  Installing := True;
  PaintBackground;
  WizardForm.NextButton.OnClick(WizardForm.NextButton);
end;

{ 勾选框也自绘。TNewCheckBox 在视觉主题下由主题引擎画标题，
  会无视 Font.Color——深色背景上就是一行几乎看不见的黑字。 }
procedure PaintCheck(Img: TBitmapImage; Caption: String; Checked: Boolean);
var
  C: TCanvas;
  W, H, Y, Box, Top: Integer;
begin
  W := Img.Width;
  H := Img.Height;
  Img.Bitmap.Width := W;
  Img.Bitmap.Height := H;
  C := Img.Bitmap.Canvas;

  for Y := 0 to H - 1 do
  begin
    C.Pen.Color := GradientAt(Img.Top + Y);
    C.MoveTo(0, Y);
    C.LineTo(W, Y);
  end;

  Box := ScaleY(16);
  Top := (H - Box) div 2;

  C.Pen.Width := 1;
  if Checked then
  begin
    C.Pen.Color := ColAccent;
    C.Brush.Color := ColAccent;
  end
  else
  begin
    C.Pen.Color := ColFieldEdge;
    C.Brush.Color := ColField;
  end;
  C.RoundRect(0, Top, Box, Top + Box, ScaleX(4), ScaleX(4));

  if Checked then
  begin
    C.Pen.Color := ColTitle;
    C.Pen.Width := ScaleX(2);
    C.MoveTo(Box * 25 div 100, Top + Box div 2);
    C.LineTo(Box * 45 div 100, Top + Box * 70 div 100);
    C.LineTo(Box * 75 div 100, Top + Box * 32 div 100);
    C.Pen.Width := 1;
  end;

  C.Brush.Style := bsClear;
  C.Font.Name := 'Microsoft YaHei';
  C.Font.Size := 9;
  C.Font.Color := ColSubtitle;
  C.TextOut(Box + ScaleX(9), (H - C.TextHeight(Caption)) div 2, Caption);
  Img.Invalidate;
end;

function MakeCheck(Caption: String; Top: Integer): TBitmapImage;
begin
  Result := TBitmapImage.Create(WizardForm);
  Result.Parent := WizardForm;
  Result.SetBounds(ScaleX(40), ScaleY(Top), ScaleX(250), ScaleY(22));
  Result.AutoSize := False;
  Result.Stretch := False;
  Result.Cursor := crHand;
end;

procedure OnToggleDesktop(Sender: TObject);
begin
  DesktopOn := not DesktopOn;
  PaintCheck(DesktopCheck, '创建桌面快捷方式', DesktopOn);
end;

procedure OnToggleRun(Sender: TObject);
begin
  RunOn := not RunOn;
  PaintCheck(RunCheck, '安装完成后运行 LingoDict', RunOn);
end;

{ Inno 每次翻页都会重设导航按钮的可见性，只在 InitializeWizard 里藏一次，
  进下一页就又冒出来；而且它把 Visible 设回 True 是在 CurPageChanged 之前，
  哪怕这里再藏一次，中间也会闪一帧（进完成页时那颗「完成」就是这么露出来的）。
  位置 Inno 不会重设，所以除了藏还要挪出可视区——挪走的控件闪不出来。 }
procedure HideNavButtons;
begin
  WizardForm.NextButton.Visible := False;
  WizardForm.BackButton.Visible := False;
  WizardForm.CancelButton.Visible := False;
end;

procedure BanishNavButtons;
begin
  WizardForm.NextButton.SetBounds(-400, -400, 10, 10);
  WizardForm.BackButton.SetBounds(-400, -400, 10, 10);
  WizardForm.CancelButton.SetBounds(-400, -400, 10, 10);
end;

procedure InitializeWizard;
begin
  { 两个勾选项的默认值要在静默守卫之前设：静默安装同样该按默认值来，
    否则 DesktopOn 保持 Boolean 的初值 False，装完连桌面图标都没有。 }
  DesktopOn := True;
  RunOn := True;

  { /SILENT 和 /VERYSILENT 下不要动向导：Inno 是靠「点 NextButton」在页间推进的，
    而下面 HideNavButtons 会把它 Visible := False——按钮点不动，Setup 就报
    「Failed to proceed to next wizard page」直接退出（退出码 1，一个文件都没装）。
    交互模式下我们自绘的按钮是直接调 NextButton.OnClick，不受可见性影响，所以没暴露。 }
  if WizardSilent then
    Exit;

  WizardForm.Caption := 'LingoDict 安装';
  WizardForm.ClientWidth := ScaleX(FormW);
  WizardForm.ClientHeight := ScaleY(FormH);
  WizardForm.Position := poScreenCenter;

  { 默认向导的整套外壳：分页、分隔线、导航按钮，全部藏掉 }
  WizardForm.OuterNotebook.Visible := False;
  WizardForm.InnerNotebook.Visible := False;
  WizardForm.Bevel.Visible := False;
  HideNavButtons;
  BanishNavButtons;

  { 读回实际值：Inno 对窗体尺寸有自己的下限，直接用设进去的值铺背景
    会在底部留出一条没盖住的按钮栏底色。 }
  CW := WizardForm.ClientWidth;
  CH := WizardForm.ClientHeight;

  Bg := TBitmapImage.Create(WizardForm);
  Bg.Parent := WizardForm;
  Bg.SetBounds(0, 0, CW, CH);
  Bg.AutoSize := False;
  Bg.Stretch := False;
  PaintBackground;
  Bg.SendToBack;

  DirPath := ExpandConstant('{localappdata}\LingoDict');
  DirLabel := TBitmapImage.Create(WizardForm);
  DirLabel.Parent := WizardForm;
  DirLabel.SetBounds(ScaleX(40), ScaleY(186), ScaleX(390), ScaleY(28));
  DirLabel.AutoSize := False;
  DirLabel.Stretch := False;
  DirLabel.Cursor := crHand;
  DirLabel.OnClick := @OnBrowse;
  PaintDirLabel;

  BrowseButton := TBitmapImage.Create(WizardForm);
  BrowseButton.Parent := WizardForm;
  BrowseButton.SetBounds(ScaleX(442), ScaleY(186), ScaleX(66), ScaleY(28));
  BrowseButton.AutoSize := False;
  BrowseButton.Stretch := False;
  BrowseButton.Cursor := crHand;
  BrowseButton.OnClick := @OnBrowse;
  PaintFlatButton(BrowseButton, '浏览…', ColField, ColSubtitle, 9);

  DesktopCheck := MakeCheck('创建桌面快捷方式', 234);
  DesktopCheck.OnClick := @OnToggleDesktop;
  PaintCheck(DesktopCheck, '创建桌面快捷方式', DesktopOn);
  RunCheck := MakeCheck('安装完成后运行 LingoDict', 262);
  RunCheck.OnClick := @OnToggleRun;
  PaintCheck(RunCheck, '安装完成后运行 LingoDict', RunOn);

  { 主按钮自绘。原生 TNewButton 由系统主题画，设 Color 没用，
    拿不到品牌蓝；TPanel 能上色但它的 OnClick 在 Inno 的 Pascal 里不触发，
    做出来是个点不动的蓝块。TBitmapImage 两样都占。 }
  GoButton := TBitmapImage.Create(WizardForm);
  GoButton.Parent := WizardForm;
  { 两个勾选项占 234..284，按钮 40 高在这段里居中 }
  GoButton.SetBounds(ScaleX(FormW - 160), ScaleY(239), ScaleX(120), ScaleY(40));
  GoButton.AutoSize := False;
  GoButton.Stretch := False;
  GoButton.Cursor := crHand;
  GoButton.OnClick := @OnGo;
  PaintGoButton('立即安装');

  { 自绘按钮当不了 Default，回车会没反应。摆一颗挪到可视区外的
    真按钮专门接管回车。 }
  EnterTrigger := TNewButton.Create(WizardForm);
  EnterTrigger.Parent := WizardForm;
  EnterTrigger.SetBounds(-200, -200, 10, 10);
  EnterTrigger.TabStop := False;
  EnterTrigger.Default := True;
  EnterTrigger.OnClick := @OnGo;

  { 状态文字：不设 Color 的话 TNewStaticText 会顶着一块 clBtnFace 白底 }
  Status := WizardForm.StatusLabel;
  Status.Parent := WizardForm;
  Status.SetBounds(ScaleX(40), CH - ScaleY(100), CW - ScaleX(80), ScaleY(20));
  Status.Color := ColInk;
  Status.Font.Name := 'Microsoft YaHei';
  Status.Font.Size := 9;
  Status.Font.Color := ColSubtitle;
  Status.Visible := False;

  { 自绘进度条：TNewProgressBar 用系统主题，是那根绿的，改不了颜色。
    两个 TPanel 叠出来就能用品牌蓝——TPanel 只是不响应点击，显示没问题。 }
  BarBack := TPanel.Create(WizardForm);
  BarBack.Parent := WizardForm;
  BarBack.SetBounds(ScaleX(40), CH - ScaleY(74), CW - ScaleX(80), ScaleY(6));
  BarBack.BevelOuter := bvNone;
  BarBack.ParentBackground := False;
  BarBack.Color := ColField;
  BarBack.Visible := False;

  BarFill := TPanel.Create(WizardForm);
  BarFill.Parent := BarBack;
  BarFill.SetBounds(0, 0, 0, BarBack.Height);
  BarFill.BevelOuter := bvNone;
  BarFill.ParentBackground := False;
  BarFill.Color := ColAccent;

  WizardForm.ProgressGauge.Visible := False;
  WizardForm.FilenameLabel.Visible := False;
end;

{ 中间那几页全部跳过：选项都收在首页上了 }
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = wpInfoBefore) or (PageID = wpSelectDir) or
            (PageID = wpSelectComponents) or (PageID = wpSelectProgramGroup) or
            (PageID = wpSelectTasks) or (PageID = wpReady) or (PageID = wpInfoAfter);
end;

procedure CurInstallProgressChanged(CurProgress, MaxProgress: Integer);
begin
  if WizardSilent then
    Exit;

  if MaxProgress > 0 then
    BarFill.Width := (BarBack.Width * CurProgress) div MaxProgress;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  { 静默安装时上面那些控件根本没建，碰它们会炸 }
  if WizardSilent then
    Exit;

  HideNavButtons;

  if CurPageID = wpInstalling then
  begin
    Status.Visible := True;
    BarBack.Visible := True;
  end
  else if CurPageID = wpFinished then
  begin
    Status.Caption := '安装完成。';
    BarFill.Width := BarBack.Width;
    PaintGoButton('开始使用');
    GoButton.Visible := True;
  end;
end;
