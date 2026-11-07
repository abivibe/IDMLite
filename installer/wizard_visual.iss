{ Visual layer only: the native wizard still owns navigation, errors and install state. }
type
  TSetupContrast = record
    Size: LongWord;
    Flags: LongWord;
    Scheme: LongWord;
  end;

function CreateRoundRectRgn(X1, Y1, X2, Y2, Width, Height: Integer): LongWord;
  external 'CreateRoundRectRgn@gdi32.dll stdcall';
function DeleteObject(Handle: LongWord): Boolean;
  external 'DeleteObject@gdi32.dll stdcall';
function SetWindowRgn(Wnd, Region: LongWord; Redraw: Boolean): Integer;
  external 'SetWindowRgn@user32.dll stdcall';
function SetTimer(Wnd, Id: LongWord; Period: LongWord; Callback: Longint): LongWord;
  external 'SetTimer@user32.dll stdcall';
function KillTimer(Wnd, Id: LongWord): Boolean;
  external 'KillTimer@user32.dll stdcall';
function GetWindowLongW(Wnd: LongWord; Index: Integer): Longint;
  external 'GetWindowLongW@user32.dll stdcall';
function SetWindowLongW(Wnd: LongWord; Index: Integer; Value: Longint): Longint;
  external 'SetWindowLongW@user32.dll stdcall';
function SetLayeredWindowAttributes(Wnd: LongWord; Key: LongWord; Alpha: Byte; Flags: LongWord): Boolean;
  external 'SetLayeredWindowAttributes@user32.dll stdcall';
function QueryAnimations(Action, Param: LongWord; var Value: Integer; Flags: LongWord): Boolean;
  external 'SystemParametersInfoW@user32.dll stdcall';
function QueryContrast(Action, Param: LongWord; var Value: TSetupContrast; Flags: LongWord): Boolean;
  external 'SystemParametersInfoW@user32.dll stdcall';

const
  SurfaceColor = $252321;
  TrackColor = $494441;
  MutedColor = $D7CECA;
  AccentColor = $FFCA9D;
  SuccessColor = $92EAB1;

var
  Ring, Gauge, FinishMark: TBitmapImage;
  InstallStatus, InstallPercent, VersionMark: TNewStaticText;
  AnimationTimer: LongWord;
  CurrentPage, SpinnerFrame, PageFade, LabelFade, PercentDone, LastGaugePercent: Integer;
  UseVisuals, AnimateVisuals: Boolean;
  NextStatus: string;

procedure PaintRing(Image: TBitmapImage; Finished: Boolean);
var
  C: TCanvas;
  S, Center, Radius, I, Step, X, Y, R: Integer;
  A: Extended;
begin
  S := Image.Width;
  Center := S div 2;
  Radius := S div 3;
  C := Image.Bitmap.Canvas;
  C.Pen.Style := psClear;
  C.Brush.Color := SurfaceColor;
  C.Rectangle(0, 0, S + 1, S + 1);
  for I := 4 downto 0 do
  begin
    R := Radius + I * ScaleX(4);
    C.Brush.Color := $332F2C - I * $020202;
    C.Ellipse(Center - R, Center - R, Center + R, Center + R);
  end;
  C.Pen.Style := psSolid;
  C.Pen.Width := ScaleX(2);
  C.Pen.Color := TrackColor;
  C.Brush.Style := bsClear;
  C.Ellipse(Center - Radius, Center - Radius, Center + Radius, Center + Radius);
  C.Brush.Style := bsSolid;
  if Finished then
  begin
    C.Pen.Color := SuccessColor;
    C.Pen.Width := ScaleX(5);
    C.MoveTo(Center - S div 7, Center);
    C.LineTo(Center - S div 24, Center + S div 9);
    C.LineTo(Center + S div 6, Center - S div 8);
  end
  else
  begin
    C.Pen.Style := psClear;
    for I := 0 to 11 do
    begin
      Step := (I + SpinnerFrame) mod 12;
      A := Step * 0.5235987756;
      X := Center + Round(Cos(A) * Radius);
      Y := Center + Round(Sin(A) * Radius);
      R := ScaleX(2);
      C.Brush.Color := TrackColor;
      if I >= 6 then C.Brush.Color := $8C6857;
      if I >= 9 then C.Brush.Color := AccentColor;
      C.Ellipse(X - R, Y - R, X + R, Y + R);
    end;
  end;
  Image.Invalidate;
end;

procedure PaintGauge;
var
  W, H, Filled: Integer;
  C: TCanvas;
begin
  if LastGaugePercent = PercentDone then Exit;
  LastGaugePercent := PercentDone;
  W := Gauge.Width;
  H := Gauge.Height;
  Filled := W * PercentDone div 100;
  C := Gauge.Bitmap.Canvas;
  C.Pen.Style := psClear;
  C.Brush.Color := SurfaceColor;
  C.Rectangle(0, 0, W + 1, H + 1);
  C.Brush.Color := TrackColor;
  C.RoundRect(0, 0, W, H, H, H);
  if Filled > 0 then
  begin
    C.Brush.Color := AccentColor;
    C.RoundRect(0, 0, Filled, H, H, H);
  end;
  Gauge.Invalidate;
end;

function FadeTextColor(Amount: Integer): TColor;
begin
  Result := SurfaceColor + Amount * $101010;
end;

procedure CenterInstallLabel(LabelControl: TNewStaticText);
begin
  LabelControl.Left := (WizardForm.InstallingPage.ClientWidth - LabelControl.Width) div 2;
end;

procedure VisualTick(Wnd: LongWord; Msg: LongWord; Id: LongWord; Tick: LongWord);
begin
  if PageFade < 5 then
  begin
    PageFade := PageFade + 1;
    SetLayeredWindowAttributes(Wnd, 0, 230 + PageFade * 5, 2);
  end;
  if CurrentPage <> wpInstalling then Exit;
  SpinnerFrame := (SpinnerFrame + 1) mod 12;
  Log('Visual paint ring');
  PaintRing(Ring, False);
  if InstallStatus.Caption <> NextStatus then
  begin
    if LabelFade > 4 then LabelFade := LabelFade - 1;
    if LabelFade <= 4 then
    begin
      InstallStatus.Caption := NextStatus;
      CenterInstallLabel(InstallStatus);
    end;
  end
  else if LabelFade < 10 then LabelFade := LabelFade + 1;
  InstallStatus.Font.Color := FadeTextColor(LabelFade);
end;

function NewImage(Parent: TWinControl; X, Y, W, H: Integer): TBitmapImage;
begin
  Result := TBitmapImage.Create(WizardForm);
  Result.Parent := Parent;
  Result.SetBounds(X, Y, W, H);
  Result.Bitmap.Width := W;
  Result.Bitmap.Height := H;
end;

function NewStatus(Parent: TWinControl; X, Y, W, H, Size: Integer): TNewStaticText;
begin
  Result := TNewStaticText.Create(WizardForm);
  Result.Parent := Parent;
  Result.AutoSize := True;
  Result.ShowAccelChar := False;
  Result.SetBounds(X, Y, W, H);
  Result.Font.Name := 'Segoe UI';
  Result.Font.Size := Size;
  Result.Font.Color := MutedColor;
end;

procedure InitializeWizard;
var
  Contrast: TSetupContrast;
  Animation, W, Mid, S, Style: Integer;
begin
  if WizardSilent then Exit;
  Log('Visual start');
  Contrast.Size := SizeOf(Contrast);
  Contrast.Flags := 0;
  Contrast.Scheme := 0;
  if QueryContrast($42, Contrast.Size, Contrast, 0) and ((Contrast.Flags and 1) <> 0) then Exit;

  Log('Visual colors');
  UseVisuals := True;
  WizardForm.BorderStyle := bsNone;
  WizardForm.Color := SurfaceColor;
  WizardForm.InstallingPage.Color := SurfaceColor;
  WizardForm.FinishedPage.Color := SurfaceColor;
  WizardForm.MainPanel.Color := SurfaceColor;
  WizardForm.StatusLabel.Visible := False;
  WizardForm.FilenameLabel.Visible := False;
  WizardForm.ProgressGauge.Visible := False;
  W := WizardForm.InstallingPage.ClientWidth;
  Mid := W div 2;
  S := ScaleX(104);
  Log('Visual ring');
  Ring := NewImage(WizardForm.InstallingPage, Mid - S div 2, ScaleY(8), S, S);
  InstallStatus := NewStatus(WizardForm.InstallingPage, 0, ScaleY(114), W, ScaleY(23), 12);
  InstallPercent := NewStatus(WizardForm.InstallingPage, 0, ScaleY(146), W, ScaleY(23), 11);
  InstallPercent.Caption := '0%';
  CenterInstallLabel(InstallPercent);
  Gauge := NewImage(WizardForm.InstallingPage, ScaleX(32), ScaleY(174), W - ScaleX(64), ScaleY(4));
  LastGaugePercent := -1;
  NextStatus := CustomMessage('CopyingFiles');
  InstallStatus.Caption := NextStatus;
  CenterInstallLabel(InstallStatus);
  LabelFade := 10;
  Log('Visual paint ring');
  PaintRing(Ring, False);
  PaintGauge;

  Log('Visual finish mark');
  S := ScaleX(88);
  FinishMark := NewImage(WizardForm.FinishedPage, ScaleX(30), ScaleY(35), S, S);
  FinishMark.Visible := False;
  PaintRing(FinishMark, True);

  VersionMark := NewStatus(WizardForm, ScaleX(16), WizardForm.NextButton.Top + ScaleY(6), ScaleX(105), ScaleY(18), 9);

  VersionMark.Caption := 'IDMLite  /  {#MyAppVersion}';

  Log('Visual timer');
  Animation := 0;
  if QueryAnimations($1042, 0, Animation, 0) and (Animation <> 0) then
  begin
    Style := GetWindowLongW(WizardForm.Handle, -20);
    SetWindowLongW(WizardForm.Handle, -20, Style or $80000);
    SetLayeredWindowAttributes(WizardForm.Handle, 0, 255, 2);
    AnimationTimer := SetTimer(WizardForm.Handle, 0, 45, CreateCallback(@VisualTick));
    AnimateVisuals := AnimationTimer <> 0;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Region: LongWord;
begin
  if not UseVisuals then Exit;
  CurrentPage := CurPageID;
  Region := CreateRoundRectRgn(0, 0, WizardForm.Width + 1, WizardForm.Height + 1, ScaleX(20), ScaleY(20));
  if (Region <> 0) and (SetWindowRgn(WizardForm.Handle, Region, True) = 0) then DeleteObject(Region);
  if CurPageID = wpFinished then
  begin
    WizardForm.WizardBitmapImage2.Visible := False;
    FinishMark.Visible := True;
    FinishMark.BringToFront;
  end;
  if AnimateVisuals then
  begin
    PageFade := 0;
    SetLayeredWindowAttributes(WizardForm.Handle, 0, 230, 2);
  end;
end;

procedure CurInstallProgressChanged(CurProgress, MaxProgress: Integer);
begin
  if (not UseVisuals) or (MaxProgress <= 0) then Exit;
  PercentDone := Round(100.0 * CurProgress / MaxProgress);
  if PercentDone < 0 then PercentDone := 0;
  if PercentDone > 100 then PercentDone := 100;
  InstallPercent.Caption := IntToStr(PercentDone) + '%';
  CenterInstallLabel(InstallPercent);
  PaintGauge;
  if WizardForm.StatusLabel.Caption <> '' then
    NextStatus := WizardForm.StatusLabel.Caption;
  if not AnimateVisuals then
  begin
    InstallStatus.Caption := NextStatus;
    CenterInstallLabel(InstallStatus);
  end;
end;

procedure DeinitializeSetup;
begin
  if AnimationTimer <> 0 then KillTimer(WizardForm.Handle, AnimationTimer);
end;



