using System.Runtime.CompilerServices;

namespace Drilling.UI.Menu;
public abstract class CBindingBase
{
    public event EventHandler? AbleBrushChanged;
    public event EventHandler? AccentBrushChanged;
    public event EventHandler? AccessChanged;
    public event EventHandler? ActionsChanged;
    public event EventHandler? ActiveChanged;
    public event EventHandler? AddressChanged;
    public event EventHandler? AfterBrushChanged;
    public event EventHandler? AkMarginXItemChanged;
    public event EventHandler? AkMarginYItemChanged;
    public event EventHandler? AlarmChanged;
    public event EventHandler? AlarmBrushChanged;
    public event EventHandler? AlarmDetailChanged;
    public event EventHandler? AlarmResetCommandChanged;
    public event EventHandler? AllInterfaceRowsChanged;
    public event EventHandler? AllManagedItemsChanged;
    public event EventHandler? AllParameterRowsChanged;
    public event EventHandler? ApplyOneHoleReviewOffsetCommandChanged;
    public event EventHandler? ApplyPreviewRowsChanged;
    public event EventHandler? ApplyRowsChanged;
    public event EventHandler? Arg1Changed;
    public event EventHandler? Arg2Changed;
    public event EventHandler? Arg3Changed;
    public event EventHandler? Arg4Changed;
    public event EventHandler? Arg5Changed;
    public event EventHandler? AttenuatorSettingItemsChanged;
    public event EventHandler? AttenuatorStateBrushChanged;
    public event EventHandler? AttenuatorStatusItemsChanged;
    public event EventHandler? AttenuatorTextChanged;
    public event EventHandler? AutoConnectionChanged;
    public event EventHandler? AutoConnectionOptionsChanged;
    public event EventHandler? AutomationControllerStatusItemsChanged;
    public event EventHandler? AxisChanged;
    public event EventHandler? AxisIdChanged;
    public event EventHandler? AxisRowsChanged;
    public event EventHandler? BackgroundBrushChanged;
    public event EventHandler? BackToCellPreviewCommandChanged;
    public event EventHandler? BackToCoordinateGlassPreviewCommandChanged;
    public event EventHandler? BackToOneHoleCellMapCommandChanged;
    public event EventHandler? BackToOneHoleHoleMapCommandChanged;
    public event EventHandler? BackToRunGlassPreviewCommandChanged;
    public event EventHandler? BackToRunShotMapCommandChanged;
    public event EventHandler? BackToSampleGlassPreviewCommandChanged;
    public event EventHandler? BaseCenterXChanged;
    public event EventHandler? BaseCenterYChanged;
    public event EventHandler? BeamOptionsChanged;
    public event EventHandler? BeamSelectionSummaryChanged;
    public event EventHandler? BetDivergenceTextChanged;
    public event EventHandler? BetMagnificationTextChanged;
    public event EventHandler? BetStateBrushChanged;
    public event EventHandler? BetTableRowsChanged;
    public event EventHandler? BlockedItemsChanged;
    public event EventHandler? BorderBrushChanged;
    public event EventHandler? BuzzerOffCommandChanged;
    public event EventHandler? CalculatedOffsetRowsChanged;
    public event EventHandler? CanApplyOneHoleReviewOffsetChanged;
    public event EventHandler? CancelCommandChanged;
    public event EventHandler? CandidateRowsChanged;
    public event EventHandler? CanOperateSelectedInterfaceChanged;
    public event EventHandler? CanReadChanged;
    public event EventHandler? CanLoadBeamInspectionResultChanged;
    public event EventHandler? CanClearLoadedBeamInspectionResultChanged;
    public event EventHandler? CanSelectBeamChanged;
    public event EventHandler? CanSelectInspectionMethodChanged;
    public event EventHandler? CanWriteChanged;
    public event EventHandler? CAppStartupChanged;
    public event EventHandler? CategoryChanged;
    public event EventHandler? CButtonCommandBehaviorChanged;
    public event EventHandler? CellMapDesignHeightChanged;
    public event EventHandler? CellMapDesignWidthChanged;
    public event EventHandler? CellNoChanged;
    public event EventHandler? CellModelTypeCountItemChanged;
    public event EventHandler? CellOverviewRowsChanged;
    public event EventHandler? CellPreviewImageChanged;
    public event EventHandler? CellPreviewLabelsChanged;
    public event EventHandler? CellShotMapImageChanged;
    public event EventHandler? CenterMoveCommandChanged;
    public event EventHandler? CenterOnCommandChanged;
    public event EventHandler? CenterXTextChanged;
    public event EventHandler? CenterYTextChanged;
    public event EventHandler? ChangeHistoryChanged;
    public event EventHandler? ChangeLimitChanged;
    public event EventHandler? ChessItemChanged;
    public event EventHandler? CInterfaceStatusRowChanged;
    public event EventHandler? ColumnChanged;
    public event EventHandler? CommandBlueChanged;
    public event EventHandler? CommandBlueBorderChanged;
    public event EventHandler? CommandDarkChanged;
    public event EventHandler? CommandDarkBorderChanged;
    public event EventHandler? CommandGreenChanged;
    public event EventHandler? CommandGreenBorderChanged;
    public event EventHandler? CommandHistoryRowsChanged;
    public event EventHandler? CommandLabelChanged;
    public event EventHandler? CommandOpacityChanged;
    public event EventHandler? CommandPropertyChanged;
    public event EventHandler? CommandRedChanged;
    public event EventHandler? CommandRedBorderChanged;
    public event EventHandler? CommandStateItemsChanged;
    public event EventHandler? CommandStateRowsChanged;
    public event EventHandler? ConnectInterfaceCommandChanged;
    public event EventHandler? ContainerHeightPropertyChanged;
    public event EventHandler? ContainerWidthPropertyChanged;
    public event EventHandler? ControllerTaskTextChanged;
    public event EventHandler? CoordinateBasisDescriptionChanged;
    public event EventHandler? CoordinateBasisOptionsChanged;
    public event EventHandler? CoordinateCellPreviewLabelsChanged;
    public event EventHandler? CoordinateGlassPreviewImageChanged;
    public event EventHandler? CoordinateGlassPreviewSummaryChanged;
    public event EventHandler? CoordinateIsCellDetailVisibleChanged;
    public event EventHandler? CoordinateIsGlassPreviewVisibleChanged;
    public event EventHandler? CoordinateSelectedBasisNameChanged;
    public event EventHandler? CoordinateSelectedCellNameChanged;
    public event EventHandler? CoordinateSelectedRecipeNameChanged;
    public event EventHandler? CoordinateSelectedShotNameChanged;
    public event EventHandler? CoordinateShotMatrixRowsChanged;
    public event EventHandler? CoordinateValueRowsChanged;
    public event EventHandler? CorrectedCenterXChanged;
    public event EventHandler? CorrectedCenterYChanged;
    public event EventHandler? CorrectionChanged;
    public event EventHandler? CPreviewCoordinateBehaviorChanged;
    public event EventHandler? CreateCommandChanged;
    public event EventHandler? CThemeManagerChanged;
    public event EventHandler? CurrentAlarmRowsChanged;
    public event EventHandler? CurrentAlarmsChanged;
    public event EventHandler? CurrentCellIndicatorTextChanged;
    public event EventHandler? CurrentDateTextChanged;
    public event EventHandler? CurrentOffsetRowsChanged;
    public event EventHandler? CurrentPositionChanged;
    public event EventHandler? CurrentReviewOffsetRecipeNameChanged;
    public event EventHandler? CurrentScreenChanged;
    public event EventHandler? CurrentSettingStateChanged;
    public event EventHandler? CurrentStepDetailsChanged;
    public event EventHandler? CurrentThemeChanged;
    public event EventHandler? CurrentTimeTextChanged;
    public event EventHandler? CurrentUserTextChanged;
    public event EventHandler? CurrentValueBrushChanged;
    public event EventHandler? CycleItemsChanged;
    public event EventHandler? DataTypeChanged;
    public event EventHandler? DefaultChanged;
    public event EventHandler? DefaultValueChanged;
    public event EventHandler? DeleteCommandChanged;
    public event EventHandler? DescriptionChanged;
    public event EventHandler? DesignHeightPropertyChanged;
    public event EventHandler? DesignWidthPropertyChanged;
    public event EventHandler? DetailChanged;
    public event EventHandler? DetailBrushChanged;
    public event EventHandler? DetailItemsChanged;
    public event EventHandler? DetailRowsChanged;
    public event EventHandler? DetailTextChanged;
    public event EventHandler? DeviceChanged;
    public event EventHandler? DeviceNoChanged;
    public event EventHandler? DeviceTabsChanged;
    public event EventHandler? DioAddressChanged;
    public event EventHandler? DioAxisNameChanged;
    public event EventHandler? DioCurrentValueChanged;
    public event EventHandler? DioLastResultChanged;
    public event EventHandler? DioNoChanged;
    public event EventHandler? DioOffCommandChanged;
    public event EventHandler? DioOnCommandChanged;
    public event EventHandler? DioReadCommandChanged;
    public event EventHandler? DirectionChanged;
    public event EventHandler? DisconnectInterfaceCommandChanged;
    public event EventHandler? DisplayAxisChanged;
    public event EventHandler? DisplayLabelChanged;
    public event EventHandler? DisplayTextChanged;
    public event EventHandler? DistortionKeyItemsChanged;
    public event EventHandler? DivChanged;
    public event EventHandler? ElapsedTimeTextChanged;
    public event EventHandler? ElementHeightPropertyChanged;
    public event EventHandler? ElementWidthPropertyChanged;
    public event EventHandler? EmptyChanged;
    public event EventHandler? EN_MENUChanged;
    public event EventHandler? EN_UI_THEMEChanged;
    public event EventHandler? EndpointChanged;
    public event EventHandler? ErrorBrushChanged;
    public event EventHandler? ErrorXChanged;
    public event EventHandler? ErrorYChanged;
    public event EventHandler? EstimatedTimeTextChanged;
    public event EventHandler? EvenHeadPreviewsChanged;
    public event EventHandler? ExecuteCommandChanged;
    public event EventHandler? ExecuteOperationCommandChanged;
    public event EventHandler? ExtraChanged;
    public event EventHandler? FileTextChanged;
    public event EventHandler? FirstXChanged;
    public event EventHandler? FirstYChanged;
    public event EventHandler? FooterStatusItemsChanged;
    public event EventHandler? GlassFrameChanged;
    public event EventHandler? GlassPreviewSummaryChanged;
    public event EventHandler? GlassSizeXItemChanged;
    public event EventHandler? GlassSizeYItemChanged;
    public event EventHandler? GridColLinesChanged;
    public event EventHandler? GridRowLinesChanged;
    public event EventHandler? GroupChanged;
    public event EventHandler? GroupItemsChanged;
    public event EventHandler? GroupTabsChanged;
    public event EventHandler? HasCommandChanged;
    public event EventHandler? HeadAssignmentAreasChanged;
    public event EventHandler? HeadCardsChanged;
    public event EventHandler? HeadGroupsChanged;
    public event EventHandler? HeadDeviceSelectorTitleChanged;
    public event EventHandler? HeaderStatusItemsChanged;
    public event EventHandler? HeadParametersChanged;
    public event EventHandler? HeadParameterVisibilityChanged;
    public event EventHandler? HeadPreviewImageChanged;
    public event EventHandler? HeadPreviewLabelsChanged;
    public event EventHandler? HeadPreviewsChanged;
    public event EventHandler? HeadPreviewSummaryTextChanged;
    public event EventHandler? HeadSelectRowsChanged;
    public event EventHandler? HeightChanged;
    public event EventHandler? HistoryChanged;
    public event EventHandler? HistoryPanelTitleChanged;
    public event EventHandler? HistoryRowsChanged;
    public event EventHandler? HomeBrushChanged;
    public event EventHandler? IconGeometryChanged;
    public event EventHandler? IdChanged;
    public event EventHandler? InspectionModeTextChanged;
    public event EventHandler? InspectionMethodItemsChanged;
    public event EventHandler? InspectionRuleTextChanged;
    public event EventHandler? InspectionRuleVisibilityChanged;
    public event EventHandler? InspectionStatusItemsChanged;
    public event EventHandler? InspectionSummaryChanged;
    public event EventHandler? InterfaceRowsChanged;
    public event EventHandler? InterfaceTypeChanged;
    public event EventHandler? InterlockItemsChanged;
    public event EventHandler? IsAlarmLayoutChanged;
    public event EventHandler? IsAttenuatorChanged;
    public event EventHandler? IsBetChanged;
    public event EventHandler? IsBooleanChanged;
    public event EventHandler? IsCellCategoryChanged;
    public event EventHandler? IsCellDetailVisibleChanged;
    public event EventHandler? IsCellPreviewVisibleChanged;
    public event EventHandler? IsControlDeviceChanged;
    public event EventHandler? IsCoordinateViewerChanged;
    public event EventHandler? IsLogViewerChanged;
    public event EventHandler? IsCorrectionLayoutChanged;
    public event EventHandler? IsAlignDistortionTabChanged;
    public event EventHandler? IsCorrectionCommandsVisibleChanged;
    public event EventHandler? IsOutputHistoryTabChanged;
    public event EventHandler? IsCurrentChanged;
    public event EventHandler? IsDioControlBusyChanged;
    public event EventHandler? IsDioControlEnabledChanged;
    public event EventHandler? IsDioOffChanged;
    public event EventHandler? IsDioOnChanged;
    public event EventHandler? IsDioTargetReadyChanged;
    public event EventHandler? IsEditableChanged;
    public event EventHandler? IsEditedChanged;
    public event EventHandler? IsEnabledChanged;
    public event EventHandler? IsGenericDeviceChanged;
    public event EventHandler? IsGenericLayoutChanged;
    public event EventHandler? IsHeadCategoryChanged;
    public event EventHandler? IsInsideGlassChanged;
    public event EventHandler? IsInterfaceTabChanged;
    public event EventHandler? IsLaserChanged;
    public event EventHandler? IsLaserCountModeChanged;
    public event EventHandler? IsLaserTimeModeChanged;
    public event EventHandler? IsMainLayoutChanged;
    public event EventHandler? IsManualLayoutChanged;
    public event EventHandler? IsManualShiftOffsetVisibleChanged;
    public event EventHandler? IsMelsecChanged;
    public event EventHandler? IsMelsecWriteLockVisibleChanged;
    public event EventHandler? IsMelsecWriteUnlockedChanged;
    public event EventHandler? IsModifiedChanged;
    public event EventHandler? IsMonitorLayoutChanged;
    public event EventHandler? IsMotorChanged;
    public event EventHandler? IsOneHoleBeamMapChanged;
    public event EventHandler? IsOneHoleCellMapChanged;
    public event EventHandler? IsOneHoleDetailMapChanged;
    public event EventHandler? IsOneHoleHoleMapChanged;
    public event EventHandler? IsOneHoleTabChanged;
    public event EventHandler? IsOtherCorrectionTabChanged;
    public event EventHandler? IsParameterTabChanged;
    public event EventHandler? IsPicoMotorChanged;
    public event EventHandler? IsPmLayoutChanged;
    public event EventHandler? IsPowerMeterChanged;
    public event EventHandler? IsProductChanged;
    public event EventHandler? IsReadOnlyChanged;
    public event EventHandler? IsRecipeLayoutChanged;
    public event EventHandler? IsReviewLayoutChanged;
    public event EventHandler? IsAllShotReviewModeChanged;
    public event EventHandler? IsRuleReviewModeChanged;
    public event EventHandler? IsBeamInspectionModeChanged;
    public event EventHandler? IsBeamSelectionVisibleChanged;
    public event EventHandler? IsLastMeasurementVisibleChanged;
    public event EventHandler? IsRunBeamDetailVisibleChanged;
    public event EventHandler? IsRunCellDetailVisibleChanged;
    public event EventHandler? IsRunGlassPreviewVisibleChanged;
    public event EventHandler? IsRunShotMapVisibleChanged;
    public event EventHandler? IsZPositionMoveEnabledChanged;
    public event EventHandler? IsRunTabChanged;
    public event EventHandler? IsEdgeSampleRuleUiChanged;
    public event EventHandler? IsFineFiveLineChanged;
    public event EventHandler? IsFineOneLineChanged;
    public event EventHandler? IsFineSampleRuleUiChanged;
    public event EventHandler? IsFineSevenLineChanged;
    public event EventHandler? IsFineThreeLineChanged;
    public event EventHandler? IsRoughCenterChanged;
    public event EventHandler? IsRoughFirstChanged;
    public event EventHandler? IsRoughLastChanged;
    public event EventHandler? IsRoughSampleRuleUiChanged;
    public event EventHandler? IsSampleCellDetailVisibleChanged;
    public event EventHandler? IsSampleGlassPreviewVisibleChanged;
    public event EventHandler? IsSampleScopeHeadChanged;
    public event EventHandler? IsSampleScopeHeadCellChanged;
    public event EventHandler? IsSampleScopeVisibleChanged;
    public event EventHandler? IsSampleSelectTabChanged;
    public event EventHandler? IsSimpleSampleRuleUiChanged;
    public event EventHandler? IsScannerStatusSelectedChanged;
    public event EventHandler? IsScannerWorkspaceChanged;
    public event EventHandler? IsManualMofTabChanged;
    public event EventHandler? IsReviewWorkspaceChanged;
    public event EventHandler? IsManualControllerTabChanged;
    public event EventHandler? ManualControllerHeadsChanged;
    public event EventHandler? IsManualMotionTabChanged;
    public event EventHandler? IsManualScanTabChanged;
    public event EventHandler? IsManualPowerMeasureTabChanged;
    public event EventHandler? IsManualStageReviewTabChanged;
    public event EventHandler? IsSelectedChanged;
    public event EventHandler? IsSettingLayoutChanged;
    public event EventHandler? IsSignChanged;
    public event EventHandler? IsSimulationChanged;
    public event EventHandler? IsStageVisionWorkspaceChanged;
    public event EventHandler? IsVisionProtocolBusyChanged;
    public event EventHandler? IsVisionFlipOptionChanged;
    public event EventHandler? ItemChanged;
    public event EventHandler? ItemTabsChanged;
    public event EventHandler? JudgeBrushChanged;
    public event EventHandler? KeyChanged;
    public event EventHandler? LabelCanvasTopChanged;
    public event EventHandler? LaserControlRowsChanged;
    public event EventHandler? LaserGateBrushChanged;
    public event EventHandler? LaserGateTextChanged;
    public event EventHandler? LaserOffCommandChanged;
    public event EventHandler? LaserOnCommandChanged;
    public event EventHandler? LaserOnModeChanged;
    public event EventHandler? LaserOnSegmentsTextChanged;
    public event EventHandler? LaserOnTimeMsecChanged;
    public event EventHandler? LaserPowerBrushChanged;
    public event EventHandler? LaserPowerTextChanged;
    public event EventHandler? LaserShotCountChanged;
    public event EventHandler? LaserShutterBrushChanged;
    public event EventHandler? LaserShutterTextChanged;
    public event EventHandler? LaserStateBrushChanged;
    public event EventHandler? LastChangedTextChanged;
    public event EventHandler? LengthChanged;
    public event EventHandler? LevelBrushChanged;
    public event EventHandler? LifecycleItemsChanged;
    public event EventHandler? LimitMinusBrushChanged;
    public event EventHandler? LimitPlusBrushChanged;
    public event EventHandler? LoadedReviewFileNameChanged;
    public event EventHandler? LoadedSettingNameChanged;
    public event EventHandler? LoadedSettingPathChanged;
    public event EventHandler? LockItemsChanged;
    public event EventHandler? MagChanged;
    public event EventHandler? MainOperatingChanged;
    public event EventHandler? ManagedItemsChanged;
    public event EventHandler? ManualChanged;
    public event EventHandler? ManualSettingsChanged;
    public event EventHandler? MatrixPointNameChanged;
    public event EventHandler? MaxChanged;
    public event EventHandler? MaxCellNumberItemChanged;
    public event EventHandler? MelsecGroupsChanged;
    public event EventHandler? MelsecReadRowsChanged;
    public event EventHandler? MelsecRowsChanged;
    public event EventHandler? MelsecWriteRowsChanged;
    public event EventHandler? MelsecWriteLockTextChanged;
    public event EventHandler? MenuChanged;
    public event EventHandler? MenusChanged;
    public event EventHandler? MetricsChanged;
    public event EventHandler? MinChanged;
    public event EventHandler? ModesChanged;
    public event EventHandler? ModelOptionsChanged;
    public event EventHandler? ModelTypeChanged;
    public event EventHandler? ModifiedBrushChanged;
    public event EventHandler? ModifiedTextChanged;
    public event EventHandler? ModifyCommandChanged;
    public event EventHandler? MonitorChanged;
    public event EventHandler? MoveCommandLabelChanged;
    public event EventHandler? MoveCountTextChanged;
    public event EventHandler? MoveStopCommandChanged;
    public event EventHandler? MultiMoveAxisRowsChanged;
    public event EventHandler? MutedChanged;
    public event EventHandler? NameChanged;
    public event EventHandler? NavigateShotGroupCommandChanged;
    public event EventHandler? NextChanged;
    public event EventHandler? NextCellCommandChanged;
    public event EventHandler? NickNameChanged;
    public event EventHandler? NoChanged;
    public event EventHandler? NoOpChanged;
    public event EventHandler? NumberChanged;
    public event EventHandler? OddHeadPreviewsChanged;
    public event EventHandler? OfflineChanged;
    public event EventHandler? OffsetCoordinateTextChanged;
    public event EventHandler? OffsetValueBrushChanged;
    public event EventHandler? OffsetXChanged;
    public event EventHandler? OffsetYChanged;
    public event EventHandler? OneHoleBeamRowsChanged;
    public event EventHandler? OneHoleCellHoleMatrixRowsChanged;
    public event EventHandler? OneHoleCellHoleRowsChanged;
    public event EventHandler? OneHoleCellPreviewLabelsChanged;
    public event EventHandler? OneHoleCurrentHoleMarkersChanged;
    public event EventHandler? OneHoleGlassPreviewImageChanged;
    public event EventHandler? OneHoleGlassPreviewSummaryChanged;
    public event EventHandler? OneHoleWorkspaceSummaryChanged;
    public event EventHandler? OneHoleWorkspaceTitleChanged;
    public event EventHandler? OnlineChanged;
    public event EventHandler? OpenChanged;
    public event EventHandler? OperationButtonsChanged;
    public event EventHandler? OperationFieldsChanged;
    public event EventHandler? OperationPanelTitleChanged;
    public event EventHandler? OpticAttenuatorVisibilityChanged;
    public event EventHandler? OpticBetVisibilityChanged;
    public event EventHandler? OpticHeadParametersChanged;
    public event EventHandler? OpticLaserVisibilityChanged;
    public event EventHandler? OpticParameterGroupsChanged;
    public event EventHandler? OpticParameterVisibilityChanged;
    public event EventHandler? OpticStatusTabsChanged;
    public event EventHandler? OptionBorderBrushChanged;
    public event EventHandler? OptionBrushChanged;
    public event EventHandler? OptionOpacityChanged;
    public event EventHandler? OptionTextChanged;
    public event EventHandler? OptionVisibilityChanged;
    public event EventHandler? OriginalValueChanged;
    public event EventHandler? ParameterChanged;
    public event EventHandler? ParameterPanelTitleChanged;
    public event EventHandler? ParameterRowsChanged;
    public event EventHandler? ParametersChanged;
    public event EventHandler? ParameterTabsChanged;
    public event EventHandler? PicoAllMotorCommandButtonsChanged;
    public event EventHandler? PicoAllMotorSelectButtonsChanged;
    public event EventHandler? PicoConnectionButtonsChanged;
    public event EventHandler? PicoErrorRowsChanged;
    public event EventHandler? PicoJogStartCommandChanged;
    public event EventHandler? PicoJogStopCommandChanged;
    public event EventHandler? PicoMotionJogButtonsChanged;
    public event EventHandler? PicoMotionMoveButtonsChanged;
    public event EventHandler? PicoMotionRelButtonsChanged;
    public event EventHandler? PicoMotionStopButtonsChanged;
    public event EventHandler? PicoMotorSelectButtonsChanged;
    public event EventHandler? PixelCountXItemChanged;
    public event EventHandler? PixelCountYItemChanged;
    public event EventHandler? PixelSizeItemChanged;
    public event EventHandler? PlacementStateChanged;
    public event EventHandler? PlanSummaryChanged;
    public event EventHandler? PmChanged;
    public event EventHandler? PointSelectRowsChanged;
    public event EventHandler? PollMsChanged;
    public event EventHandler? PositionMoveCommandChanged;
    public event EventHandler? PositionMoveItemsChanged;
    public event EventHandler? PositionRowsChanged;
    public event EventHandler? PositionXPropertyChanged;
    public event EventHandler? PositionYPropertyChanged;
    public event EventHandler? PowerBrushChanged;
    public event EventHandler? PowerMeterGraphLineChanged;
    public event EventHandler? PowerMeterGraphMaxTextChanged;
    public event EventHandler? PowerMeterGraphMinTextChanged;
    public event EventHandler? PowerMeterGraphTargetTextChanged;
    public event EventHandler? PowerMeterTargetLineYChanged;
    public event EventHandler? PowerMeterTuneMessageChanged;
    public event EventHandler? PowerMeterTuneRowsChanged;
    public event EventHandler? PowerMeterTuneStateChanged;
    public event EventHandler? PreviousChanged;
    public event EventHandler? PreviousCellCommandChanged;
    public event EventHandler? PrimaryTextChanged;
    public event EventHandler? ProcessPitchItemChanged;
    public event EventHandler? ProcessResultBrushChanged;
    public event EventHandler? ProcessResultValueChanged;
    public event EventHandler? ProcessSequenceItemsChanged;
    public event EventHandler? ProcessStepChanged;
    public event EventHandler? ProcessSummaryItemsChanged;
    public event EventHandler? ProductAlignItemsChanged;
    public event EventHandler? ProductDistortionItemsChanged;
    public event EventHandler? ProductGlassItemsChanged;
    public event EventHandler? ProductHistoryRowsChanged;
    public event EventHandler? ProductItemsChanged;
    public event EventHandler? ProductProcessItemsChanged;
    public event EventHandler? ProductRecipeItemsChanged;
    public event EventHandler? ProductReviewItemsChanged;
    public event EventHandler? ProductReviewRowsChanged;
    public event EventHandler? ProgressPercentChanged;
    public event EventHandler? ProgressPercentTextChanged;
    public event EventHandler? ProgressTextChanged;
    public event EventHandler? PwmDeviceRowsChanged;
    public event EventHandler? PwmRunButtonsChanged;
    public event EventHandler? PwmSettingRowsChanged;
    public event EventHandler? QuickSelectCommandChanged;
    public event EventHandler? RangeVisibilityChanged;
    public event EventHandler? ReadValueChanged;
    public event EventHandler? RecipeChanged;
    public event EventHandler? RecipeFilesChanged;
    public event EventHandler? RecipeFilesPageTextChanged;
    public event EventHandler? RecipeListChanged;
    public event EventHandler? RecipeNameChanged;
    public event EventHandler? RecipePreviewImageChanged;
    public event EventHandler? RecoveryCommandsChanged;
    public event EventHandler? ReloadCommandChanged;
    public event EventHandler? RenameCommandChanged;
    public event EventHandler? ResultBrushChanged;
    public event EventHandler? ResultItemsChanged;
    public event EventHandler? ResultMessageChanged;
    public event EventHandler? ResultMatrixChanged;
    public event EventHandler? IsReviewResultDeferredChanged;
    public event EventHandler? ResultPasswordChanged;
    public event EventHandler? ResultRowsChanged;
    public event EventHandler? ResultValueChanged;
    public event EventHandler? RetryCommandChanged;
    public event EventHandler? ReviewChanged;
    public event EventHandler? ReviewLoadStatusChanged;
    public event EventHandler? ReviewLoadStatusBrushChanged;
    public event EventHandler? ReviewResultRowsChanged;
    public event EventHandler? ReviewSetsChanged;
    public event EventHandler? RotationChanged;
    public event EventHandler? RowChanged;
    public event EventHandler? RowBrushChanged;
    public event EventHandler? RunBeamRowsChanged;
    public event EventHandler? RunCellShotMatrixRowsChanged;
    public event EventHandler? RunCellShotRowsChanged;
    public event EventHandler? RunCellPreviewLabelsChanged;
    public event EventHandler? InspectionHeadRowsChanged;
    public event EventHandler? IsOneShotReviewModeChanged;
    public event EventHandler? IsShotSelectionModeChanged;
    public event EventHandler? IsRunCommandsVisibleChanged;
    public event EventHandler? OneShotSelectionChanged;
    public event EventHandler? OneShotStatusChanged;
    public event EventHandler? OneShotResultTextChanged;
    public event EventHandler? SelectedOneShotMethodChanged;
    public event EventHandler? InspectionShotSummaryChanged;
    public event EventHandler? InspectionProgressTextChanged;
    public event EventHandler? IsInspectionExecutionViewVisibleChanged;
    public event EventHandler? IsInspectionSelectionControlsVisibleChanged;
    public event EventHandler? RunCurrentHoleMarkersChanged;
    public event EventHandler? RunGlassPreviewImageChanged;
    public event EventHandler? RunGlassPreviewInspectionOverlayImageChanged;
    public event EventHandler? RunGlassPreviewSummaryChanged;
    public event EventHandler? RunWorkspaceSummaryChanged;
    public event EventHandler? RunWorkspaceTitleChanged;
    public event EventHandler? SampleCorrectionTargetChanged;
    public event EventHandler? SampleCorrectionTitleChanged;
    public event EventHandler? SampleCellShotMatrixRowsChanged;
    public event EventHandler? SampleCellShotRowsChanged;
    public event EventHandler? SampleCellOptionsChanged;
    public event EventHandler? SampleCellPreviewLabelsChanged;
    public event EventHandler? SampleGlassPreviewImageChanged;
    public event EventHandler? SampleGlassPreviewInspectionOverlayImageChanged;
    public event EventHandler? SampleGlassPreviewSummaryChanged;
    public event EventHandler? SampleHeadOptionsChanged;
    public event EventHandler? SampleInspectionShotCountChanged;
    public event EventHandler? SampleMapColumnCountChanged;
    public event EventHandler? SampleOffsetApplyCountChanged;
    public event EventHandler? SamplePlanStatusChanged;
    public event EventHandler? SampleReferenceCellChanged;
    public event EventHandler? SampleReferenceHeadChanged;
    public event EventHandler? SampleReferenceShotChanged;
    public event EventHandler? SampleRuleDescriptionChanged;
    public event EventHandler? SampleRuleNameChanged;
    public event EventHandler? SampleRuleSelectionSummaryChanged;
    public event EventHandler? SampleReferenceShotMarkersChanged;
    public event EventHandler? SampleReferenceThirdLabelChanged;
    public event EventHandler? SampleTargetSectionTitleChanged;
    public event EventHandler? SampleWorkspaceSummaryChanged;
    public event EventHandler? SampleWorkspaceTitleChanged;
    public event EventHandler? SaveCommandChanged;
    public event EventHandler? ScaleChanged;
    public event EventHandler? ScannerAxesChanged;
    public event EventHandler? ScannerStatusItemsChanged;
    public event EventHandler? ScannerStatusHeadTabsChanged;
    public event EventHandler? ScannerStatusVisibilityChanged;
    public event EventHandler? ScriptStatusChanged;
    public event EventHandler? ScriptStatusTextChanged;
    public event EventHandler? ScriptTaskStatusItemsChanged;
    public event EventHandler? SectionsChanged;
    public event EventHandler? SelectCategoryCommandChanged;
    public event EventHandler? SelectCellCommandChanged;
    public event EventHandler? SelectCommandChanged;
    public event EventHandler? SelectCoordinateBasisCommandChanged;
    public event EventHandler? SelectCoordinateCellCommandChanged;
    public event EventHandler? SelectCoordinateShotCommandChanged;
    public event EventHandler? SelectedAxisIdChanged;
    public event EventHandler? SelectedAxisRowChanged;
    public event EventHandler? SelectedBeamChanged;
    public event EventHandler? SelectedInspectionMethodChanged;
    public event EventHandler? SelectedCellExcludedTargetTextChanged;
    public event EventHandler? SelectedCellHasExcludedTargetsChanged;
    public event EventHandler? SelectedCellNavigationTextChanged;
    public event EventHandler? SelectedCellShotGroupTitleChanged;
    public event EventHandler? SelectedGroupChanged;
    public event EventHandler? SelectedHeadChanged;
    public event EventHandler? SelectedHeadDeviceNameChanged;
    public event EventHandler? SelectedHeadItemsChanged;
    public event EventHandler? SelectedInterfaceRowChanged;
    public event EventHandler? SelectedLaserNameChanged;
    public event EventHandler? SelectedLaserNumberChanged;
    public event EventHandler? SelectedMenuChanged;
    public event EventHandler? SelectedPointItemsChanged;
    public event EventHandler? SelectedRecipeFileChanged;
    public event EventHandler? SelectedRunShotNameChanged;
    public event EventHandler? SelectedSampleRuleUiChanged;
    public event EventHandler? SelectedSampleCellChanged;
    public event EventHandler? SelectedSampleHeadChanged;
    public event EventHandler? SelectedShotDetailImageChanged;
    public event EventHandler? SelectedShotGroupChanged;
    public event EventHandler? SelectedShotGroupBeamTextChanged;
    public event EventHandler? SelectedShotGroupCenterTextChanged;
    public event EventHandler? SelectedShotGroupGridTextChanged;
    public event EventHandler? SelectedShotGroupIndicatorTextChanged;
    public event EventHandler? SelectedShotGroupPitchTextChanged;
    public event EventHandler? SelectedShotGroupPixelWindowTextChanged;
    public event EventHandler? SelectedShotGroupStateTextChanged;
    public event EventHandler? SelectedTabChanged;
    public event EventHandler? SelectedVisionProtocolCommandChanged;
    public event EventHandler? SelectGroupCommandChanged;
    public event EventHandler? SelectHeadCommandChanged;
    public event EventHandler? SelectHeadDeviceCommandChanged;
    public event EventHandler? SelectionSummaryItemsChanged;
    public event EventHandler? SelectManualWorkspaceCommandChanged;
    public event EventHandler? SelectMelsecGroupCommandChanged;
    public event EventHandler? SelectModeCommandChanged;
    public event EventHandler? SelectOneHoleCellCommandChanged;
    public event EventHandler? SelectOneHoleCommandChanged;
    public event EventHandler? SelectParameterTabCommandChanged;
    public event EventHandler? SelectPreviewCellCommandChanged;
    public event EventHandler? SelectRunBeamCommandChanged;
    public event EventHandler? SelectRunCellCommandChanged;
    public event EventHandler? SelectRunShotCommandChanged;
    public event EventHandler? SelectSampleCellCommandChanged;
    public event EventHandler? SelectSettingCommandChanged;
    public event EventHandler? SelectShapeCommandChanged;
    public event EventHandler? SelectTabCommandChanged;
    public event EventHandler? ServoBrushChanged;
    public event EventHandler? SetSampleHoleSelectionCommandChanged;
    public event EventHandler? SettingChanged;
    public event EventHandler? SettingFilesChanged;
    public event EventHandler? SettingFilesPageTextChanged;
    public event EventHandler? SettingParametersChanged;
    public event EventHandler? ShapeDirectionChanged;
    public event EventHandler? ShapeNameChanged;
    public event EventHandler? ShapeOffsetXChanged;
    public event EventHandler? ShapeOffsetYChanged;
    public event EventHandler? ShapeScanItemsChanged;
    public event EventHandler? ShapeSizeChanged;
    public event EventHandler? ShapeStartCommandChanged;
    public event EventHandler? ShapeStopCommandChanged;
    public event EventHandler? ShiftOffsetXChanged;
    public event EventHandler? ShiftOffsetYChanged;
    public event EventHandler? ShotCentersChanged;
    public event EventHandler? ShotDetailDesignHeightChanged;
    public event EventHandler? ShotDetailDesignWidthChanged;
    public event EventHandler? CellShotNoChanged;
    public event EventHandler? ShotGroupPatternSummaryChanged;
    public event EventHandler? ShotGroupRowsChanged;
    public event EventHandler? ShotTextChanged;
    public event EventHandler? ShowCycleControlsChanged;
    public event EventHandler? SimulChanged;
    public event EventHandler? SimulBrushChanged;
    public event EventHandler? SourceGroupChanged;
    public event EventHandler? SourceRowsChanged;
    public event EventHandler? SplitBeamCountItemChanged;
    public event EventHandler? SpotSizeChanged;
    public event EventHandler? ST_MONITOR_MELSEC_ROWChanged;
    public event EventHandler? ST_MONITOR_PARAMETER_ROWChanged;
    public event EventHandler? StageAxesChanged;
    public event EventHandler? StageHomeCommandChanged;
    public event EventHandler? StageMoveCommandChanged;
    public event EventHandler? StageStopCommandChanged;
    public event EventHandler? StartCommandChanged;
    public event EventHandler? StartTimeChanged;
    public event EventHandler? StateChanged;
    public event EventHandler? StateBrushChanged;
    public event EventHandler? StateRowsChanged;
    public event EventHandler? StateTextChanged;
    public event EventHandler? StatusBrushChanged;
    public event EventHandler? StatusMessageChanged;
    public event EventHandler? StatusPanelTitleChanged;
    public event EventHandler? StatusRowsChanged;
    public event EventHandler? StopCommandChanged;
    public event EventHandler? SubtitleChanged;
    public event EventHandler? SummaryItemsChanged;
    public event EventHandler? SummaryRowsChanged;
    public event EventHandler? SystemSectionChanged;
    public event EventHandler? TabItemsChanged;
    public event EventHandler? TabsChanged;
    public event EventHandler? TargetGxChanged;
    public event EventHandler? TargetGyChanged;
    public event EventHandler? TargetGzChanged;
    public event EventHandler? TargetItemsChanged;
    public event EventHandler? TargetPositionChanged;
    public event EventHandler? TextBrushChanged;
    public event EventHandler? ThemeModeTextChanged;
    public event EventHandler? ThemeToggleTextChanged;
    public event EventHandler? TitleChanged;
    public event EventHandler? ToggleOperationModeCommandChanged;
    public event EventHandler? ToggleMelsecWriteLockCommandChanged;
    public event EventHandler? TogglePointCommandChanged;
    public event EventHandler? TogglePreviewHeadCommandChanged;
    public event EventHandler? ToggleThemeCommandChanged;
    public event EventHandler? TotalShotsTextChanged;
    public event EventHandler? TrendChanged;
    public event EventHandler? TrendBarsChanged;
    public event EventHandler? TrendPanelTitleChanged;
    public event EventHandler? TrendPointsChanged;
    public event EventHandler? TypeChanged;
    public event EventHandler? TypeBrushChanged;
    public event EventHandler? TypeOptionsChanged;
    public event EventHandler? UnitChanged;
    public event EventHandler? UseBrushChanged;
    public event EventHandler? UsesSelectionEditorChanged;
    public event EventHandler? UseStretchScalePropertyChanged;
    public event EventHandler? UsesWriteSelectionChanged;
    public event EventHandler? UseTextChanged;
    public event EventHandler? ValueChanged;
    public event EventHandler? ValueBrushChanged;
    public event EventHandler? ValueOptionsChanged;
    public event EventHandler? ValueStateChanged;
    public event EventHandler? VisionProtocolCommandDescriptionChanged;
    public event EventHandler? VisionProtocolEndpointChanged;
    public event EventHandler? VisionProtocolHasNoArgumentsChanged;
    public event EventHandler? VisionProtocolInspectionTypeChanged;
    public event EventHandler? VisionProtocolIsFlyingReadyChanged;
    public event EventHandler? VisionProtocolKeepAliveChanged;
    public event EventHandler? VisionProtocolMarkRowCountTextChanged;
    public event EventHandler? VisionProtocolRequestPreviewChanged;
    public event EventHandler? VisionProtocolStatusChanged;
    public event EventHandler? VisionProtocolUsesMarkRowsChanged;
    public event EventHandler? ZPositionMoveCommandChanged;
    public event EventHandler? WaitChanged;
    public event EventHandler? WriteInputTypeChanged;
    public event EventHandler? WriteMelsecCommandChanged;
    public event EventHandler? WriteValueChanged;
    public event EventHandler? WriteValueOptionsChanged;
    public event EventHandler? XLabelChanged;
    public event EventHandler? YLabelChanged;
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected bool SetSequenceProperty<T>(ref IReadOnlyList<T> field, IReadOnlyList<T> value, [CallerMemberName] string? propertyName = null)
    {
        if (ReferenceEquals(field, value) || field.SequenceEqual(value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        switch (propertyName)
        {
            case "AbleBrush":
                AbleBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "AccentBrush":
                AccentBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Access":
                AccessChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Actions":
                ActionsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Active":
                ActiveChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Address":
                AddressChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "AfterBrush":
                AfterBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "AkMarginXItem":
                AkMarginXItemChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "AkMarginYItem":
                AkMarginYItemChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Alarm":
                AlarmChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "AlarmBrush":
                AlarmBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "AlarmDetail":
                AlarmDetailChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "AlarmResetCommand":
                AlarmResetCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "AllInterfaceRows":
                AllInterfaceRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "AllManagedItems":
                AllManagedItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "AllParameterRows":
                AllParameterRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ApplyOneHoleReviewOffsetCommand":
                ApplyOneHoleReviewOffsetCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ApplyPreviewRows":
                ApplyPreviewRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ApplyRows":
                ApplyRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Arg1":
                Arg1Changed?.Invoke(this, EventArgs.Empty);
                break;
            case "Arg2":
                Arg2Changed?.Invoke(this, EventArgs.Empty);
                break;
            case "Arg3":
                Arg3Changed?.Invoke(this, EventArgs.Empty);
                break;
            case "Arg4":
                Arg4Changed?.Invoke(this, EventArgs.Empty);
                break;
            case "Arg5":
                Arg5Changed?.Invoke(this, EventArgs.Empty);
                break;
            case "AttenuatorSettingItems":
                AttenuatorSettingItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "AttenuatorStateBrush":
                AttenuatorStateBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "AttenuatorStatusItems":
                AttenuatorStatusItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "AttenuatorText":
                AttenuatorTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "AutoConnection":
                AutoConnectionChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "AutoConnectionOptions":
                AutoConnectionOptionsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "AutomationControllerStatusItems":
                AutomationControllerStatusItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Axis":
                AxisChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "AxisId":
                AxisIdChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "AxisRows":
                AxisRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "BackgroundBrush":
                BackgroundBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "BackToCellPreviewCommand":
                BackToCellPreviewCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "BackToCoordinateGlassPreviewCommand":
                BackToCoordinateGlassPreviewCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "BackToOneHoleCellMapCommand":
                BackToOneHoleCellMapCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "BackToOneHoleHoleMapCommand":
                BackToOneHoleHoleMapCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "BackToRunGlassPreviewCommand":
                BackToRunGlassPreviewCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "BackToRunShotMapCommand":
                BackToRunShotMapCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "BackToSampleGlassPreviewCommand":
                BackToSampleGlassPreviewCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "BaseCenterX":
                BaseCenterXChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "BaseCenterY":
                BaseCenterYChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "BeamOptions":
                BeamOptionsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "BeamSelectionSummary":
                BeamSelectionSummaryChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "BetDivergenceText":
                BetDivergenceTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "BetMagnificationText":
                BetMagnificationTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "BetStateBrush":
                BetStateBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "BetTableRows":
                BetTableRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "BlockedItems":
                BlockedItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "BorderBrush":
                BorderBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "BuzzerOffCommand":
                BuzzerOffCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CalculatedOffsetRows":
                CalculatedOffsetRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CanApplyOneHoleReviewOffset":
                CanApplyOneHoleReviewOffsetChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CancelCommand":
                CancelCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CandidateRows":
                CandidateRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CanOperateSelectedInterface":
                CanOperateSelectedInterfaceChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CanRead":
                CanReadChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CanLoadBeamInspectionResult":
                CanLoadBeamInspectionResultChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CanClearLoadedBeamInspectionResult":
                CanClearLoadedBeamInspectionResultChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CanSelectBeam":
                CanSelectBeamChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CanSelectInspectionMethod":
                CanSelectInspectionMethodChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CanWrite":
                CanWriteChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CAppStartup":
                CAppStartupChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Category":
                CategoryChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CButtonCommandBehavior":
                CButtonCommandBehaviorChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CellMapDesignHeight":
                CellMapDesignHeightChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CellMapDesignWidth":
                CellMapDesignWidthChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CellNo":
                CellNoChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CellModelTypeCountItem":
                CellModelTypeCountItemChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CellOverviewRows":
                CellOverviewRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CellPreviewImage":
                CellPreviewImageChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CellPreviewLabels":
                CellPreviewLabelsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CellShotMapImage":
                CellShotMapImageChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CenterMoveCommand":
                CenterMoveCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CenterOnCommand":
                CenterOnCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CenterXText":
                CenterXTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CenterYText":
                CenterYTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ChangeHistory":
                ChangeHistoryChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ChangeLimit":
                ChangeLimitChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ChessItem":
                ChessItemChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CInterfaceStatusRow":
                CInterfaceStatusRowChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Column":
                ColumnChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CommandBlue":
                CommandBlueChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CommandBlueBorder":
                CommandBlueBorderChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CommandDark":
                CommandDarkChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CommandDarkBorder":
                CommandDarkBorderChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CommandGreen":
                CommandGreenChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CommandGreenBorder":
                CommandGreenBorderChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CommandHistoryRows":
                CommandHistoryRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CommandLabel":
                CommandLabelChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CommandOpacity":
                CommandOpacityChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CommandProperty":
                CommandPropertyChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CommandRed":
                CommandRedChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CommandRedBorder":
                CommandRedBorderChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CommandStateItems":
                CommandStateItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CommandStateRows":
                CommandStateRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ConnectInterfaceCommand":
                ConnectInterfaceCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ContainerHeightProperty":
                ContainerHeightPropertyChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ContainerWidthProperty":
                ContainerWidthPropertyChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ControllerTaskText":
                ControllerTaskTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CoordinateBasisDescription":
                CoordinateBasisDescriptionChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CoordinateBasisOptions":
                CoordinateBasisOptionsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CoordinateCellPreviewLabels":
                CoordinateCellPreviewLabelsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CoordinateGlassPreviewImage":
                CoordinateGlassPreviewImageChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CoordinateGlassPreviewSummary":
                CoordinateGlassPreviewSummaryChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CoordinateIsCellDetailVisible":
                CoordinateIsCellDetailVisibleChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CoordinateIsGlassPreviewVisible":
                CoordinateIsGlassPreviewVisibleChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CoordinateSelectedBasisName":
                CoordinateSelectedBasisNameChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CoordinateSelectedCellName":
                CoordinateSelectedCellNameChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CoordinateSelectedRecipeName":
                CoordinateSelectedRecipeNameChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CoordinateSelectedShotName":
                CoordinateSelectedShotNameChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CoordinateShotMatrixRows":
                CoordinateShotMatrixRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CoordinateValueRows":
                CoordinateValueRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CorrectedCenterX":
                CorrectedCenterXChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CorrectedCenterY":
                CorrectedCenterYChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Correction":
                CorrectionChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CPreviewCoordinateBehavior":
                CPreviewCoordinateBehaviorChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CreateCommand":
                CreateCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CThemeManager":
                CThemeManagerChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CurrentAlarmRows":
                CurrentAlarmRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CurrentAlarms":
                CurrentAlarmsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CurrentCellIndicatorText":
                CurrentCellIndicatorTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CurrentDateText":
                CurrentDateTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CurrentOffsetRows":
                CurrentOffsetRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CurrentPosition":
                CurrentPositionChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CurrentReviewOffsetRecipeName":
                CurrentReviewOffsetRecipeNameChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CurrentScreen":
                CurrentScreenChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CurrentSettingState":
                CurrentSettingStateChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CurrentStepDetails":
                CurrentStepDetailsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CurrentTheme":
                CurrentThemeChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CurrentTimeText":
                CurrentTimeTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CurrentUserText":
                CurrentUserTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CurrentValueBrush":
                CurrentValueBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CycleItems":
                CycleItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "DataType":
                DataTypeChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Default":
                DefaultChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "DefaultValue":
                DefaultValueChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "DeleteCommand":
                DeleteCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Description":
                DescriptionChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "DesignHeightProperty":
                DesignHeightPropertyChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "DesignWidthProperty":
                DesignWidthPropertyChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Detail":
                DetailChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "DetailBrush":
                DetailBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "DetailItems":
                DetailItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "DetailRows":
                DetailRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "DetailText":
                DetailTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Device":
                DeviceChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "DeviceNo":
                DeviceNoChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "DeviceTabs":
                DeviceTabsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "DioAddress":
                DioAddressChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "DioAxisName":
                DioAxisNameChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "DioCurrentValue":
                DioCurrentValueChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "DioLastResult":
                DioLastResultChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "DioNo":
                DioNoChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "DioOffCommand":
                DioOffCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "DioOnCommand":
                DioOnCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "DioReadCommand":
                DioReadCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Direction":
                DirectionChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "DisconnectInterfaceCommand":
                DisconnectInterfaceCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "DisplayAxis":
                DisplayAxisChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "DisplayLabel":
                DisplayLabelChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "DisplayText":
                DisplayTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "DistortionKeyItems":
                DistortionKeyItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Div":
                DivChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ElapsedTimeText":
                ElapsedTimeTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ElementHeightProperty":
                ElementHeightPropertyChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ElementWidthProperty":
                ElementWidthPropertyChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Empty":
                EmptyChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "EN_MENU":
                EN_MENUChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "EN_UI_THEME":
                EN_UI_THEMEChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Endpoint":
                EndpointChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ErrorBrush":
                ErrorBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ErrorX":
                ErrorXChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ErrorY":
                ErrorYChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "EstimatedTimeText":
                EstimatedTimeTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "EvenHeadPreviews":
                EvenHeadPreviewsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ExecuteCommand":
                ExecuteCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ExecuteOperationCommand":
                ExecuteOperationCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Extra":
                ExtraChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "FileText":
                FileTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "FirstX":
                FirstXChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "FirstY":
                FirstYChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "FooterStatusItems":
                FooterStatusItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "GlassFrame":
                GlassFrameChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "GlassPreviewSummary":
                GlassPreviewSummaryChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "GlassSizeXItem":
                GlassSizeXItemChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "GlassSizeYItem":
                GlassSizeYItemChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "GridColLines":
                GridColLinesChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "GridRowLines":
                GridRowLinesChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Group":
                GroupChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "GroupItems":
                GroupItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "GroupTabs":
                GroupTabsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "HasCommand":
                HasCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "HeadAssignmentAreas":
                HeadAssignmentAreasChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "HeadCards":
                HeadCardsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "HeadGroups":
                HeadGroupsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "HeadDeviceSelectorTitle":
                HeadDeviceSelectorTitleChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "HeaderStatusItems":
                HeaderStatusItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "HeadParameters":
                HeadParametersChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "HeadParameterVisibility":
                HeadParameterVisibilityChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "HeadPreviewImage":
                HeadPreviewImageChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "HeadPreviewLabels":
                HeadPreviewLabelsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "HeadPreviews":
                HeadPreviewsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "HeadPreviewSummaryText":
                HeadPreviewSummaryTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "HeadSelectRows":
                HeadSelectRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Height":
                HeightChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "History":
                HistoryChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "HistoryPanelTitle":
                HistoryPanelTitleChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "HistoryRows":
                HistoryRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "HomeBrush":
                HomeBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IconGeometry":
                IconGeometryChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Id":
                IdChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "InspectionModeText":
                InspectionModeTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "InspectionMethodItems":
                InspectionMethodItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "InspectionRuleText":
                InspectionRuleTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "InspectionRuleVisibility":
                InspectionRuleVisibilityChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "InspectionStatusItems":
                InspectionStatusItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "InspectionSummary":
                InspectionSummaryChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "InterfaceRows":
                InterfaceRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "InterfaceType":
                InterfaceTypeChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "InterlockItems":
                InterlockItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsAlarmLayout":
                IsAlarmLayoutChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsAttenuator":
                IsAttenuatorChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsBet":
                IsBetChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsBoolean":
                IsBooleanChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsCellCategory":
                IsCellCategoryChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsCellDetailVisible":
                IsCellDetailVisibleChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsCellPreviewVisible":
                IsCellPreviewVisibleChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsControlDevice":
                IsControlDeviceChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsLogViewer":
                IsLogViewerChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsCoordinateViewer":
                IsCoordinateViewerChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsCorrectionLayout":
                IsCorrectionLayoutChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsAlignDistortionTab":
                IsAlignDistortionTabChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsCorrectionCommandsVisible":
                IsCorrectionCommandsVisibleChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsOutputHistoryTab":
                IsOutputHistoryTabChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsCurrent":
                IsCurrentChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsDioControlBusy":
                IsDioControlBusyChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsDioControlEnabled":
                IsDioControlEnabledChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsDioOff":
                IsDioOffChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsDioOn":
                IsDioOnChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsDioTargetReady":
                IsDioTargetReadyChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsEditable":
                IsEditableChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsEdited":
                IsEditedChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsEnabled":
                IsEnabledChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsGenericDevice":
                IsGenericDeviceChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsGenericLayout":
                IsGenericLayoutChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsHeadCategory":
                IsHeadCategoryChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsInsideGlass":
                IsInsideGlassChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsInterfaceTab":
                IsInterfaceTabChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsLaser":
                IsLaserChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsLaserCountMode":
                IsLaserCountModeChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsLaserTimeMode":
                IsLaserTimeModeChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsMainLayout":
                IsMainLayoutChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsManualLayout":
                IsManualLayoutChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsManualShiftOffsetVisible":
                IsManualShiftOffsetVisibleChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsMelsec":
                IsMelsecChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsMelsecWriteLockVisible":
                IsMelsecWriteLockVisibleChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsMelsecWriteUnlocked":
                IsMelsecWriteUnlockedChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsModified":
                IsModifiedChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsMonitorLayout":
                IsMonitorLayoutChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsMotor":
                IsMotorChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsOneHoleBeamMap":
                IsOneHoleBeamMapChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsOneHoleCellMap":
                IsOneHoleCellMapChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsOneHoleDetailMap":
                IsOneHoleDetailMapChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsOneHoleHoleMap":
                IsOneHoleHoleMapChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsOneHoleTab":
                IsOneHoleTabChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsOtherCorrectionTab":
                IsOtherCorrectionTabChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsParameterTab":
                IsParameterTabChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsPicoMotor":
                IsPicoMotorChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsPmLayout":
                IsPmLayoutChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsPowerMeter":
                IsPowerMeterChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsProduct":
                IsProductChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsReadOnly":
                IsReadOnlyChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsRecipeLayout":
                IsRecipeLayoutChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsReviewLayout":
                IsReviewLayoutChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsAllShotReviewMode":
                IsAllShotReviewModeChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsRuleReviewMode":
                IsRuleReviewModeChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsBeamInspectionMode":
                IsBeamInspectionModeChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsBeamSelectionVisible":
                IsBeamSelectionVisibleChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsLastMeasurementVisible":
                IsLastMeasurementVisibleChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsRunBeamDetailVisible":
                IsRunBeamDetailVisibleChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsRunCellDetailVisible":
                IsRunCellDetailVisibleChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsRunGlassPreviewVisible":
                IsRunGlassPreviewVisibleChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsRunShotMapVisible":
                IsRunShotMapVisibleChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsZPositionMoveEnabled":
                IsZPositionMoveEnabledChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsRunTab":
                IsRunTabChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsEdgeSampleRuleUi":
                IsEdgeSampleRuleUiChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsFineFiveLine":
                IsFineFiveLineChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsFineOneLine":
                IsFineOneLineChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsFineSampleRuleUi":
                IsFineSampleRuleUiChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsFineSevenLine":
                IsFineSevenLineChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsFineThreeLine":
                IsFineThreeLineChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsRoughCenter":
                IsRoughCenterChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsRoughFirst":
                IsRoughFirstChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsRoughLast":
                IsRoughLastChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsRoughSampleRuleUi":
                IsRoughSampleRuleUiChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsSampleCellDetailVisible":
                IsSampleCellDetailVisibleChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsSampleGlassPreviewVisible":
                IsSampleGlassPreviewVisibleChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsSampleScopeHead":
                IsSampleScopeHeadChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsSampleScopeHeadCell":
                IsSampleScopeHeadCellChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsSampleScopeVisible":
                IsSampleScopeVisibleChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsSampleSelectTab":
                IsSampleSelectTabChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsSimpleSampleRuleUi":
                IsSimpleSampleRuleUiChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsScannerStatusSelected":
                IsScannerStatusSelectedChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsScannerWorkspace":
                IsScannerWorkspaceChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsManualMofTab":
                IsManualMofTabChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsReviewWorkspace":
                IsReviewWorkspaceChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsManualControllerTab":
                IsManualControllerTabChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ManualControllerHeads":
                ManualControllerHeadsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsManualMotionTab":
                IsManualMotionTabChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsManualScanTab":
                IsManualScanTabChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsManualPowerMeasureTab":
                IsManualPowerMeasureTabChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsManualStageReviewTab":
                IsManualStageReviewTabChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsSelected":
                IsSelectedChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsSettingLayout":
                IsSettingLayoutChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsSign":
                IsSignChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsSimulation":
                IsSimulationChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsStageVisionWorkspace":
                IsStageVisionWorkspaceChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsVisionProtocolBusy":
                IsVisionProtocolBusyChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsVisionFlipOption":
                IsVisionFlipOptionChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Item":
                ItemChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ItemTabs":
                ItemTabsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "JudgeBrush":
                JudgeBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Key":
                KeyChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "LabelCanvasTop":
                LabelCanvasTopChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "LaserControlRows":
                LaserControlRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "LaserGateBrush":
                LaserGateBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "LaserGateText":
                LaserGateTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "LaserOffCommand":
                LaserOffCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "LaserOnCommand":
                LaserOnCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "LaserOnMode":
                LaserOnModeChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "LaserOnSegmentsText":
                LaserOnSegmentsTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "LaserOnTimeMsec":
                LaserOnTimeMsecChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "LaserPowerBrush":
                LaserPowerBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "LaserPowerText":
                LaserPowerTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "LaserShotCount":
                LaserShotCountChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "LaserShutterBrush":
                LaserShutterBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "LaserShutterText":
                LaserShutterTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "LaserStateBrush":
                LaserStateBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "LastChangedText":
                LastChangedTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Length":
                LengthChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "LevelBrush":
                LevelBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "LifecycleItems":
                LifecycleItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "LimitMinusBrush":
                LimitMinusBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "LimitPlusBrush":
                LimitPlusBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "LoadedReviewFileName":
                LoadedReviewFileNameChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "LoadedSettingName":
                LoadedSettingNameChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "LoadedSettingPath":
                LoadedSettingPathChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "LockItems":
                LockItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Mag":
                MagChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "MainOperating":
                MainOperatingChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ManagedItems":
                ManagedItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Manual":
                ManualChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ManualSettings":
                ManualSettingsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "MatrixPointName":
                MatrixPointNameChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Max":
                MaxChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "MaxCellNumberItem":
                MaxCellNumberItemChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "MelsecGroups":
                MelsecGroupsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "MelsecReadRows":
                MelsecReadRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "MelsecRows":
                MelsecRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "MelsecWriteRows":
                MelsecWriteRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "MelsecWriteLockText":
                MelsecWriteLockTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Menu":
                MenuChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Menus":
                MenusChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Metrics":
                MetricsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Min":
                MinChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Modes":
                ModesChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ModelOptions":
                ModelOptionsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ModelType":
                ModelTypeChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ModifiedBrush":
                ModifiedBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ModifiedText":
                ModifiedTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ModifyCommand":
                ModifyCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Monitor":
                MonitorChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "MoveCommandLabel":
                MoveCommandLabelChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "MoveCountText":
                MoveCountTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "MoveStopCommand":
                MoveStopCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "MultiMoveAxisRows":
                MultiMoveAxisRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Muted":
                MutedChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Name":
                NameChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "NavigateShotGroupCommand":
                NavigateShotGroupCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Next":
                NextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "NextCellCommand":
                NextCellCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "NickName":
                NickNameChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "No":
                NoChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "NoOp":
                NoOpChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Number":
                NumberChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "OddHeadPreviews":
                OddHeadPreviewsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Offline":
                OfflineChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "OffsetCoordinateText":
                OffsetCoordinateTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "OffsetValueBrush":
                OffsetValueBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "OffsetX":
                OffsetXChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "OffsetY":
                OffsetYChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "OneHoleBeamRows":
                OneHoleBeamRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "OneHoleCellHoleMatrixRows":
                OneHoleCellHoleMatrixRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "OneHoleCellHoleRows":
                OneHoleCellHoleRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "OneHoleCellPreviewLabels":
                OneHoleCellPreviewLabelsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "OneHoleCurrentHoleMarkers":
                OneHoleCurrentHoleMarkersChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "OneHoleGlassPreviewImage":
                OneHoleGlassPreviewImageChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "OneHoleGlassPreviewSummary":
                OneHoleGlassPreviewSummaryChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "OneHoleWorkspaceSummary":
                OneHoleWorkspaceSummaryChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "OneHoleWorkspaceTitle":
                OneHoleWorkspaceTitleChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Online":
                OnlineChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Open":
                OpenChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "OperationButtons":
                OperationButtonsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "OperationFields":
                OperationFieldsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "OperationPanelTitle":
                OperationPanelTitleChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "OpticAttenuatorVisibility":
                OpticAttenuatorVisibilityChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "OpticBetVisibility":
                OpticBetVisibilityChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "OpticHeadParameters":
                OpticHeadParametersChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "OpticLaserVisibility":
                OpticLaserVisibilityChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "OpticParameterGroups":
                OpticParameterGroupsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "OpticParameterVisibility":
                OpticParameterVisibilityChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "OpticStatusTabs":
                OpticStatusTabsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "OptionBorderBrush":
                OptionBorderBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "OptionBrush":
                OptionBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "OptionOpacity":
                OptionOpacityChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "OptionText":
                OptionTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "OptionVisibility":
                OptionVisibilityChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "OriginalValue":
                OriginalValueChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Parameter":
                ParameterChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ParameterPanelTitle":
                ParameterPanelTitleChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ParameterRows":
                ParameterRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Parameters":
                ParametersChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ParameterTabs":
                ParameterTabsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PicoAllMotorCommandButtons":
                PicoAllMotorCommandButtonsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PicoAllMotorSelectButtons":
                PicoAllMotorSelectButtonsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PicoConnectionButtons":
                PicoConnectionButtonsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PicoErrorRows":
                PicoErrorRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PicoJogStartCommand":
                PicoJogStartCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PicoJogStopCommand":
                PicoJogStopCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PicoMotionJogButtons":
                PicoMotionJogButtonsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PicoMotionMoveButtons":
                PicoMotionMoveButtonsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PicoMotionRelButtons":
                PicoMotionRelButtonsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PicoMotionStopButtons":
                PicoMotionStopButtonsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PicoMotorSelectButtons":
                PicoMotorSelectButtonsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PixelCountXItem":
                PixelCountXItemChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PixelCountYItem":
                PixelCountYItemChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PixelSizeItem":
                PixelSizeItemChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PlacementState":
                PlacementStateChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PlanSummary":
                PlanSummaryChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Pm":
                PmChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PointSelectRows":
                PointSelectRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PollMs":
                PollMsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PositionMoveCommand":
                PositionMoveCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PositionMoveItems":
                PositionMoveItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PositionRows":
                PositionRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PositionXProperty":
                PositionXPropertyChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PositionYProperty":
                PositionYPropertyChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PowerBrush":
                PowerBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PowerMeterGraphLine":
                PowerMeterGraphLineChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PowerMeterGraphMaxText":
                PowerMeterGraphMaxTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PowerMeterGraphMinText":
                PowerMeterGraphMinTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PowerMeterGraphTargetText":
                PowerMeterGraphTargetTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PowerMeterTargetLineY":
                PowerMeterTargetLineYChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PowerMeterTuneMessage":
                PowerMeterTuneMessageChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PowerMeterTuneRows":
                PowerMeterTuneRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PowerMeterTuneState":
                PowerMeterTuneStateChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Previous":
                PreviousChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PreviousCellCommand":
                PreviousCellCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PrimaryText":
                PrimaryTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ProcessPitchItem":
                ProcessPitchItemChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ProcessResultBrush":
                ProcessResultBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ProcessResultValue":
                ProcessResultValueChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ProcessSequenceItems":
                ProcessSequenceItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ProcessStep":
                ProcessStepChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ProcessSummaryItems":
                ProcessSummaryItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ProductAlignItems":
                ProductAlignItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ProductDistortionItems":
                ProductDistortionItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ProductGlassItems":
                ProductGlassItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ProductHistoryRows":
                ProductHistoryRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ProductItems":
                ProductItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ProductProcessItems":
                ProductProcessItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ProductRecipeItems":
                ProductRecipeItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ProductReviewItems":
                ProductReviewItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ProductReviewRows":
                ProductReviewRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ProgressPercent":
                ProgressPercentChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ProgressPercentText":
                ProgressPercentTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ProgressText":
                ProgressTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PwmDeviceRows":
                PwmDeviceRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PwmRunButtons":
                PwmRunButtonsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "PwmSettingRows":
                PwmSettingRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "QuickSelectCommand":
                QuickSelectCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "RangeVisibility":
                RangeVisibilityChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ReadValue":
                ReadValueChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Recipe":
                RecipeChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "RecipeFiles":
                RecipeFilesChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "RecipeFilesPageText":
                RecipeFilesPageTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "RecipeList":
                RecipeListChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "RecipeName":
                RecipeNameChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "RecipePreviewImage":
                RecipePreviewImageChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "RecoveryCommands":
                RecoveryCommandsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ReloadCommand":
                ReloadCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "RenameCommand":
                RenameCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ResultBrush":
                ResultBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ResultItems":
                ResultItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ResultMessage":
                ResultMessageChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ResultMatrix":
                ResultMatrixChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsReviewResultDeferred":
                IsReviewResultDeferredChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ResultPassword":
                ResultPasswordChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ResultRows":
                ResultRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ResultValue":
                ResultValueChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "RetryCommand":
                RetryCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Review":
                ReviewChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ReviewLoadStatus":
                ReviewLoadStatusChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ReviewLoadStatusBrush":
                ReviewLoadStatusBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ReviewResultRows":
                ReviewResultRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ReviewSets":
                ReviewSetsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Rotation":
                RotationChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Row":
                RowChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "RowBrush":
                RowBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "RunBeamRows":
                RunBeamRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "RunCellShotMatrixRows":
                RunCellShotMatrixRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "RunCellShotRows":
                RunCellShotRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "RunCellPreviewLabels":
                RunCellPreviewLabelsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsOneShotReviewMode":
                IsOneShotReviewModeChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsShotSelectionMode":
                IsShotSelectionModeChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsRunCommandsVisible":
                IsRunCommandsVisibleChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "OneShotSelection":
                OneShotSelectionChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "OneShotStatus":
                OneShotStatusChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "OneShotResultText":
                OneShotResultTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectedOneShotMethod":
                SelectedOneShotMethodChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "InspectionHeadRows":
                InspectionHeadRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "InspectionShotSummary":
                InspectionShotSummaryChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "InspectionProgressText":
                InspectionProgressTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsInspectionExecutionViewVisible":
                IsInspectionExecutionViewVisibleChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "IsInspectionSelectionControlsVisible":
                IsInspectionSelectionControlsVisibleChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "RunCurrentHoleMarkers":
                RunCurrentHoleMarkersChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "RunGlassPreviewImage":
                RunGlassPreviewImageChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "RunGlassPreviewInspectionOverlayImage":
                RunGlassPreviewInspectionOverlayImageChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "RunGlassPreviewSummary":
                RunGlassPreviewSummaryChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "RunWorkspaceSummary":
                RunWorkspaceSummaryChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "RunWorkspaceTitle":
                RunWorkspaceTitleChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SampleCorrectionTarget":
                SampleCorrectionTargetChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SampleCorrectionTitle":
                SampleCorrectionTitleChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SampleCellShotMatrixRows":
                SampleCellShotMatrixRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SampleCellShotRows":
                SampleCellShotRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SampleCellOptions":
                SampleCellOptionsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SampleCellPreviewLabels":
                SampleCellPreviewLabelsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SampleGlassPreviewImage":
                SampleGlassPreviewImageChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SampleGlassPreviewInspectionOverlayImage":
                SampleGlassPreviewInspectionOverlayImageChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SampleGlassPreviewSummary":
                SampleGlassPreviewSummaryChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SampleHeadOptions":
                SampleHeadOptionsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SampleInspectionShotCount":
                SampleInspectionShotCountChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SampleMapColumnCount":
                SampleMapColumnCountChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SampleOffsetApplyCount":
                SampleOffsetApplyCountChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SamplePlanStatus":
                SamplePlanStatusChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SampleReferenceCell":
                SampleReferenceCellChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SampleReferenceHead":
                SampleReferenceHeadChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SampleReferenceShot":
                SampleReferenceShotChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SampleRuleDescription":
                SampleRuleDescriptionChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SampleRuleName":
                SampleRuleNameChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SampleRuleSelectionSummary":
                SampleRuleSelectionSummaryChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SampleReferenceShotMarkers":
                SampleReferenceShotMarkersChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SampleReferenceThirdLabel":
                SampleReferenceThirdLabelChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SampleTargetSectionTitle":
                SampleTargetSectionTitleChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SampleWorkspaceSummary":
                SampleWorkspaceSummaryChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SampleWorkspaceTitle":
                SampleWorkspaceTitleChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SaveCommand":
                SaveCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Scale":
                ScaleChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ScannerAxes":
                ScannerAxesChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ScannerStatusItems":
                ScannerStatusItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ScannerStatusHeadTabs":
                ScannerStatusHeadTabsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ScannerStatusVisibility":
                ScannerStatusVisibilityChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ScriptStatus":
                ScriptStatusChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ScriptStatusText":
                ScriptStatusTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ScriptTaskStatusItems":
                ScriptTaskStatusItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Sections":
                SectionsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectCategoryCommand":
                SelectCategoryCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectCellCommand":
                SelectCellCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectCommand":
                SelectCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectCoordinateBasisCommand":
                SelectCoordinateBasisCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectCoordinateCellCommand":
                SelectCoordinateCellCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectCoordinateShotCommand":
                SelectCoordinateShotCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectedAxisId":
                SelectedAxisIdChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectedAxisRow":
                SelectedAxisRowChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectedBeam":
                SelectedBeamChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectedInspectionMethod":
                SelectedInspectionMethodChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectedCellExcludedTargetText":
                SelectedCellExcludedTargetTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectedCellHasExcludedTargets":
                SelectedCellHasExcludedTargetsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectedCellNavigationText":
                SelectedCellNavigationTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectedCellShotGroupTitle":
                SelectedCellShotGroupTitleChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectedGroup":
                SelectedGroupChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectedHead":
                SelectedHeadChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectedHeadDeviceName":
                SelectedHeadDeviceNameChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectedHeadItems":
                SelectedHeadItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectedInterfaceRow":
                SelectedInterfaceRowChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectedLaserName":
                SelectedLaserNameChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectedLaserNumber":
                SelectedLaserNumberChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectedMenu":
                SelectedMenuChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectedPointItems":
                SelectedPointItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectedRecipeFile":
                SelectedRecipeFileChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectedRunShotName":
                SelectedRunShotNameChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectedSampleRuleUi":
                SelectedSampleRuleUiChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectedSampleCell":
                SelectedSampleCellChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectedSampleHead":
                SelectedSampleHeadChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectedShotDetailImage":
                SelectedShotDetailImageChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectedShotGroup":
                SelectedShotGroupChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectedShotGroupBeamText":
                SelectedShotGroupBeamTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectedShotGroupCenterText":
                SelectedShotGroupCenterTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectedShotGroupGridText":
                SelectedShotGroupGridTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectedShotGroupIndicatorText":
                SelectedShotGroupIndicatorTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectedShotGroupPitchText":
                SelectedShotGroupPitchTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectedShotGroupPixelWindowText":
                SelectedShotGroupPixelWindowTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectedShotGroupStateText":
                SelectedShotGroupStateTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectedTab":
                SelectedTabChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectedVisionProtocolCommand":
                SelectedVisionProtocolCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectGroupCommand":
                SelectGroupCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectHeadCommand":
                SelectHeadCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectHeadDeviceCommand":
                SelectHeadDeviceCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectionSummaryItems":
                SelectionSummaryItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectManualWorkspaceCommand":
                SelectManualWorkspaceCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectMelsecGroupCommand":
                SelectMelsecGroupCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectModeCommand":
                SelectModeCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectOneHoleCellCommand":
                SelectOneHoleCellCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectOneHoleCommand":
                SelectOneHoleCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectParameterTabCommand":
                SelectParameterTabCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectPreviewCellCommand":
                SelectPreviewCellCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectRunBeamCommand":
                SelectRunBeamCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectRunCellCommand":
                SelectRunCellCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectRunShotCommand":
                SelectRunShotCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectSampleCellCommand":
                SelectSampleCellCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectSettingCommand":
                SelectSettingCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectShapeCommand":
                SelectShapeCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SelectTabCommand":
                SelectTabCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ServoBrush":
                ServoBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SetSampleHoleSelectionCommand":
                SetSampleHoleSelectionCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Setting":
                SettingChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SettingFiles":
                SettingFilesChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SettingFilesPageText":
                SettingFilesPageTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SettingParameters":
                SettingParametersChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ShapeDirection":
                ShapeDirectionChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ShapeName":
                ShapeNameChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ShapeOffsetX":
                ShapeOffsetXChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ShapeOffsetY":
                ShapeOffsetYChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ShapeScanItems":
                ShapeScanItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ShapeSize":
                ShapeSizeChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ShapeStartCommand":
                ShapeStartCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ShapeStopCommand":
                ShapeStopCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ShiftOffsetX":
                ShiftOffsetXChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ShiftOffsetY":
                ShiftOffsetYChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ShotCenters":
                ShotCentersChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ShotDetailDesignHeight":
                ShotDetailDesignHeightChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ShotDetailDesignWidth":
                ShotDetailDesignWidthChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "CellShotNo":
                CellShotNoChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ShotGroupPatternSummary":
                ShotGroupPatternSummaryChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ShotGroupRows":
                ShotGroupRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ShotText":
                ShotTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ShowCycleControls":
                ShowCycleControlsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Simul":
                SimulChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SimulBrush":
                SimulBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SourceGroup":
                SourceGroupChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SourceRows":
                SourceRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SplitBeamCountItem":
                SplitBeamCountItemChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SpotSize":
                SpotSizeChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ST_MONITOR_MELSEC_ROW":
                ST_MONITOR_MELSEC_ROWChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ST_MONITOR_PARAMETER_ROW":
                ST_MONITOR_PARAMETER_ROWChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "StageAxes":
                StageAxesChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "StageHomeCommand":
                StageHomeCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "StageMoveCommand":
                StageMoveCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "StageStopCommand":
                StageStopCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "StartCommand":
                StartCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "StartTime":
                StartTimeChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "State":
                StateChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "StateBrush":
                StateBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "StateRows":
                StateRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "StateText":
                StateTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "StatusBrush":
                StatusBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "StatusMessage":
                StatusMessageChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "StatusPanelTitle":
                StatusPanelTitleChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "StatusRows":
                StatusRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "StopCommand":
                StopCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Subtitle":
                SubtitleChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SummaryItems":
                SummaryItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SummaryRows":
                SummaryRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "SystemSection":
                SystemSectionChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "TabItems":
                TabItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Tabs":
                TabsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "TargetGx":
                TargetGxChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "TargetGy":
                TargetGyChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "TargetGz":
                TargetGzChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "TargetItems":
                TargetItemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "TargetPosition":
                TargetPositionChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "TextBrush":
                TextBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ThemeModeText":
                ThemeModeTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ThemeToggleText":
                ThemeToggleTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Title":
                TitleChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ToggleOperationModeCommand":
                ToggleOperationModeCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ToggleMelsecWriteLockCommand":
                ToggleMelsecWriteLockCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "TogglePointCommand":
                TogglePointCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "TogglePreviewHeadCommand":
                TogglePreviewHeadCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ToggleThemeCommand":
                ToggleThemeCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "TotalShotsText":
                TotalShotsTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Trend":
                TrendChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "TrendBars":
                TrendBarsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "TrendPanelTitle":
                TrendPanelTitleChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "TrendPoints":
                TrendPointsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Type":
                TypeChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "TypeBrush":
                TypeBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "TypeOptions":
                TypeOptionsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Unit":
                UnitChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "UseBrush":
                UseBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "UsesSelectionEditor":
                UsesSelectionEditorChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "UseStretchScaleProperty":
                UseStretchScalePropertyChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "UsesWriteSelection":
                UsesWriteSelectionChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "UseText":
                UseTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Value":
                ValueChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ValueBrush":
                ValueBrushChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ValueOptions":
                ValueOptionsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ValueState":
                ValueStateChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "VisionProtocolCommandDescription":
                VisionProtocolCommandDescriptionChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "VisionProtocolEndpoint":
                VisionProtocolEndpointChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "VisionProtocolHasNoArguments":
                VisionProtocolHasNoArgumentsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "VisionProtocolInspectionType":
                VisionProtocolInspectionTypeChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "VisionProtocolIsFlyingReady":
                VisionProtocolIsFlyingReadyChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "VisionProtocolKeepAlive":
                VisionProtocolKeepAliveChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "VisionProtocolMarkRowCountText":
                VisionProtocolMarkRowCountTextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "VisionProtocolRequestPreview":
                VisionProtocolRequestPreviewChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "VisionProtocolStatus":
                VisionProtocolStatusChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "VisionProtocolUsesMarkRows":
                VisionProtocolUsesMarkRowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "ZPositionMoveCommand":
                ZPositionMoveCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "Wait":
                WaitChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "WriteInputType":
                WriteInputTypeChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "WriteMelsecCommand":
                WriteMelsecCommandChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "WriteValue":
                WriteValueChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "WriteValueOptions":
                WriteValueOptionsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "XLabel":
                XLabelChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "YLabel":
                YLabelChanged?.Invoke(this, EventArgs.Empty);
                break;
        }
    }
}
