using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace VamTimeline
{
    public class BulkScreen : ScreenBase
    {
        public const string ScreenName = "Bulk";

        private const string _offsetControllerUILabel = "Start offset controllers mode...";
        private const string _offsetControllerUIOffsetLabel = "Apply recorded offset...";

        private static bool _offsetting;
        private static string _lastOffsetMode;
        private static AtomAnimationClip _lastAnim;
        private static float _lastLength = -1f;
        private static float _lastStart = -1f;
        private static float _lastEnd = -1f;
        private static OffsetOperations.Snapshot _offsetSnapshot;

        // Pattern selection state
        private static bool _usePatternSelection;
        private static int _lastPatternInterval = 2;
        private static int _lastPatternOffset;
        private static HashSet<float> _patternSelectedKeyframes = new HashSet<float>();

        public override string screenId => ScreenName;

        private JSONStorableFloat _startJSON;
        private JSONStorableFloat _endJSON;
        private JSONStorableString _selectionJSON;
        private JSONStorableStringChooser _changeCurveJSON;
        private JSONStorableStringChooser _offsetModeJSON;
        private UIDynamicButton _offsetControllerUI;

        // Pattern selection UI
        private JSONStorableBool _usePatternSelectionJSON;
        private JSONStorableFloat _patternIntervalJSON;
        private JSONStorableFloat _patternOffsetJSON;
        private UIDynamicToggle _usePatternSelectionUI;
        private UIDynamicSlider _patternIntervalUI;
        private UIDynamicSlider _patternOffsetUI;

        public override void Init(IAtomPlugin plugin, object arg)
        {
            base.Init(plugin, arg);

            CreateChangeScreenButton("<b><</b> <i>Back</i>", MoreScreen.ScreenName);

            InitSelectionUI();

            InitPatternSelectionUI();

            InitBulkClipboardUI();

            InitChangeCurveUI();

            InitOffsetUI();

            // Init

            _startJSON.valNoCallback = _lastStart == -1f ? 0f : Mathf.Min(_lastStart, current.animationLength);
            _endJSON.valNoCallback = _lastEnd == -1f ? current.animationLength : Mathf.Min(_lastEnd, current.animationLength);
            if (_endJSON.val <= _startJSON.val || _lastAnim != current || _lastLength != current.animationLength)
            {
                _startJSON.valNoCallback = 0f;
                _endJSON.valNoCallback = current.animationLength;
            }
            animationEditContext.animation.animatables.onTargetsSelectionChanged.AddListener(OnTargetsSelectionChanged);
            OnTargetsSelectionChanged();
        }

        private void InitOffsetUI()
        {
            _offsetModeJSON = new JSONStorableStringChooser("Offset mode", new List<string> { OffsetOperations.ChangePivotMode, OffsetOperations.OffsetMode, OffsetOperations.RepositionMode }, _lastOffsetMode ?? OffsetOperations.RepositionMode, "Offset mode", val => _lastOffsetMode = val);
            prefabFactory.CreatePopup(_offsetModeJSON, false, true, 230f, true);

            _offsetControllerUI = prefabFactory.CreateButton(_offsetting ? _offsetControllerUIOffsetLabel : _offsetControllerUILabel);
            _offsetControllerUI.button.onClick.AddListener(OffsetController);
        }

        private void InitBulkClipboardUI()
        {
            var deleteUI = prefabFactory.CreateButton("Delete frame(s)");
            deleteUI.button.onClick.AddListener(() => CopyDeleteSelected(false, true));

            var cutUI = prefabFactory.CreateButton("Cut frame(s)");
            cutUI.button.onClick.AddListener(() => CopyDeleteSelected(true, true));

            var copyUI = prefabFactory.CreateButton("Copy frame(s)");
            copyUI.button.onClick.AddListener(() => CopyDeleteSelected(true, false));

            var pasteUI = prefabFactory.CreateButton("Paste frame(s)");
            pasteUI.button.onClick.AddListener(() => plugin.pasteJSON.actionCallback());
        }

        private void InitSelectionUI()
        {
            _startJSON = new JSONStorableFloat("Selection starts at", 0f, val =>
            {
                var closest = animationEditContext.GetAllOrSelectedTargets().Select(t => t.GetTimeClosestTo(val)).OrderBy(t => Mathf.Abs(val - t)).FirstOrDefault();
                _startJSON.valNoCallback = closest;
                if (_startJSON.val > _endJSON.val) _endJSON.valNoCallback = _startJSON.val;
                SelectionModified();

            }, 0f, current.animationLength);
            prefabFactory.CreateSlider(_startJSON);

            _endJSON = new JSONStorableFloat("Selection ends at", 0f, val =>
            {
                var closest = animationEditContext.GetAllOrSelectedTargets().Select(t => t.GetTimeClosestTo(val)).OrderBy(t => Mathf.Abs(val - t)).FirstOrDefault();
                _endJSON.valNoCallback = closest == 0 ? current.animationLength : closest;
                if (_endJSON.val < _startJSON.val) _startJSON.valNoCallback = _endJSON.val;
                SelectionModified();
            }, 0f, current.animationLength);
            prefabFactory.CreateSlider(_endJSON);

            var markScrubberRangeUI = prefabFactory.CreateButton("Select visible range");
            markScrubberRangeUI.button.onClick.AddListener(() =>
            {
                _startJSON.val = animationEditContext.scrubberRange.rangeBegin;
                _endJSON.val = animationEditContext.scrubberRange.rangeBegin + animationEditContext.scrubberRange.rangeDuration;
            });

            var markSelectionStartUI = prefabFactory.CreateButton("Start at current time");
            markSelectionStartUI.button.onClick.AddListener(() => _startJSON.val = current.clipTime);

            var markSelectionEndUI = prefabFactory.CreateButton("End at current time");
            markSelectionEndUI.button.onClick.AddListener(() => _endJSON.val = animationEditContext.clipTime);

            _selectionJSON = new JSONStorableString("Selected frames", "")
            {
                isStorable = false
            };
            var selectionUI = prefabFactory.CreateTextField(_selectionJSON);
            selectionUI.height = 100f;
        }

        private void InitPatternSelectionUI()
        {
            prefabFactory.CreateSpacer();

            _usePatternSelectionJSON = new JSONStorableBool("Use pattern selection", _usePatternSelection, val =>
            {
                _usePatternSelection = val;
                UpdatePatternSelectionUIVisibility();
                if (val)
                    ApplyPatternSelection();
                SelectionModified();
            });
            _usePatternSelectionUI = prefabFactory.CreateToggle(_usePatternSelectionJSON);

            // Get max keyframes count from selected targets
            var maxKeyframes = GetMaxKeyframeCount();

            _patternIntervalJSON = new JSONStorableFloat("Select every N frames", _lastPatternInterval, val =>
            {
                _lastPatternInterval = Mathf.RoundToInt(val);
                _patternIntervalJSON.valNoCallback = _lastPatternInterval;
                if (_usePatternSelection)
                    ApplyPatternSelection();
                SelectionModified();
            }, 2f, Mathf.Max(2f, maxKeyframes - 1), false);
            _patternIntervalUI = prefabFactory.CreateSlider(_patternIntervalJSON);
            _patternIntervalUI.slider.wholeNumbers = true;

            _patternOffsetJSON = new JSONStorableFloat("Starting at keyframe", _lastPatternOffset, val =>
            {
                _lastPatternOffset = Mathf.RoundToInt(val);
                _patternOffsetJSON.valNoCallback = _lastPatternOffset;
                if (_usePatternSelection)
                    ApplyPatternSelection();
                SelectionModified();
            }, 0f, Mathf.Max(0f, maxKeyframes - 1), false);
            _patternOffsetUI = prefabFactory.CreateSlider(_patternOffsetJSON);
            _patternOffsetUI.slider.wholeNumbers = true;

            UpdatePatternSelectionUIVisibility();
        }

        private void UpdatePatternSelectionUIVisibility()
        {
            if (_patternIntervalUI != null)
                _patternIntervalUI.slider.interactable = _usePatternSelection;
            if (_patternOffsetUI != null)
                _patternOffsetUI.slider.interactable = _usePatternSelection;
        }

        private int GetMaxKeyframeCount()
        {
            var maxKeyframes = 2;
            foreach (var target in animationEditContext.GetAllOrSelectedTargets())
            {
                var keyframes = target.GetAllKeyframesTime();
                if (keyframes.Length > maxKeyframes)
                    maxKeyframes = keyframes.Length;
            }
            return maxKeyframes;
        }

        private void ApplyPatternSelection()
        {
            _patternSelectedKeyframes.Clear();

            var interval = _lastPatternInterval;
            var offset = _lastPatternOffset;

            foreach (var target in animationEditContext.GetAllOrSelectedTargets())
            {
                var keyframes = target.GetAllKeyframesTime();
                for (var i = offset; i < keyframes.Length; i += interval)
                {
                    _patternSelectedKeyframes.Add(keyframes[i]);
                }
            }
        }

        private bool IsKeyframeSelected(float keyTime)
        {
            if (_usePatternSelection)
            {
                // Check if this keyframe is in our pattern selection (with small epsilon for floating point comparison)
                foreach (var selectedTime in _patternSelectedKeyframes)
                {
                    if (Mathf.Abs(selectedTime - keyTime) < 0.0001f)
                        return true;
                }
                return false;
            }
            else
            {
                // Use time range selection
                return keyTime >= _startJSON.valNoCallback && keyTime <= _endJSON.valNoCallback;
            }
        }

        private void InitChangeCurveUI()
        {
            _changeCurveJSON = new JSONStorableStringChooser("Change curve", CurveTypeValues.choicesList, "", "Change curve", ChangeCurve);
            var curveTypeUI = prefabFactory.CreatePopup(_changeCurveJSON, false, false);
            curveTypeUI.popupPanelHeight = 280f;
        }

        #region Callbacks

        private void SelectionModified()
        {
            var sb = new StringBuilder();
            if (_usePatternSelection)
            {
                sb.AppendLine($"Pattern: every {_lastPatternInterval} frames, starting at #{_lastPatternOffset}");
            }
            foreach (var target in animationEditContext.GetAllOrSelectedTargets())
            {
                var involvedKeyframes = 0;
                var keyframes = target.GetAllKeyframesTime();
                for (var key = 0; key < keyframes.Length; key++)
                {
                    var keyTime = keyframes[key];
                    if (IsKeyframeSelected(keyTime))
                        involvedKeyframes++;
                }
                if (involvedKeyframes > 0)
                    sb.AppendLine($"{target.name}: {involvedKeyframes} keyframes");
            }
            _selectionJSON.val = sb.ToString();
            _lastStart = _startJSON.val;
            _lastEnd = _endJSON.val;
            _lastAnim = current;
            _lastLength = current.animationLength;
        }

        public void CopyDeleteSelected(bool copy, bool delete)
        {
            plugin.animationEditContext.clipboard.Clear();
            plugin.animationEditContext.clipboard.time = _usePatternSelection && _patternSelectedKeyframes.Count > 0
                ? _patternSelectedKeyframes.Min()
                : _startJSON.valNoCallback;
            foreach (var target in animationEditContext.GetAllOrSelectedTargets())
            {
                target.StartBulkUpdates();
                try
                {
                    var keyframes = target.GetAllKeyframesTime();
                    for (var key = keyframes.Length - 1; key >= 0; key--)
                    {
                        var keyTime = keyframes[key];
                        if (!IsKeyframeSelected(keyTime)) continue;

                        if (copy)
                        {
                            plugin.animationEditContext.clipboard.entries.Insert(0, AtomAnimationClip.Copy(keyTime, animationEditContext.GetAllOrSelectedTargets().ToList()));
                        }
                        if (delete && !keyTime.IsSameFrame(0) && !keyTime.IsSameFrame(current.animationLength))
                        {
                            target.DeleteFrame(keyTime);
                        }
                    }
                }
                finally
                {
                    target.EndBulkUpdates();
                }
            }
        }

        public void ChangeCurve(string val)
        {
            if (string.IsNullOrEmpty(val)) return;
            _changeCurveJSON.valNoCallback = "";

            foreach (var target in animationEditContext.GetAllOrSelectedTargets().OfType<ICurveAnimationTarget>())
            {
                target.StartBulkUpdates();
                try
                {
                    var leadCurve = target.GetLeadCurve();
                    for (var key = 0; key < leadCurve.length; key++)
                    {
                        var keyTime = leadCurve.GetKeyframeByKey(key).time;
                        if (IsKeyframeSelected(keyTime))
                        {
                            target.ChangeCurveByTime(keyTime, CurveTypeValues.ToInt(val));
                        }
                    }
                }
                finally
                {
                    target.EndBulkUpdates();
                }
            }
        }

        private void OffsetController()
        {
            if (animation.isPlaying) return;

            if (_offsetting)
                ApplyOffset();
            else
                StartRecordOffset();
        }

        private void StartRecordOffset()
        {
            // Validate current time is within selection
            if (_usePatternSelection)
            {
                if (!IsKeyframeSelected(current.clipTime))
                {
                    SuperController.LogError("Timeline: Cannot offset, current time is not one of the pattern-selected keyframes");
                    return;
                }
            }
            else
            {
                if (current.clipTime < _startJSON.val || current.clipTime > _endJSON.val)
                {
                    SuperController.LogError("Timeline: Cannot offset, current time is outside of the bounds of the selection");
                    return;
                }
            }

            _offsetSnapshot = operations.Offset().Start(current.clipTime, animationEditContext.GetAllOrSelectedTargets().OfType<FreeControllerV3AnimationTarget>(), plugin.containingAtom.mainController, _offsetModeJSON.val);

            if (_offsetSnapshot == null) return;

            _offsetControllerUI.label = _offsetControllerUIOffsetLabel;
            _offsetting = true;
        }

        private void ApplyOffset()
        {
            _offsetting = false;
            _offsetControllerUI.label = _offsetControllerUILabel;

            if (animationEditContext.clipTime != _offsetSnapshot.clipboard.time)
            {
                SuperController.LogError($"Timeline: Time changed. Please move controllers within a single frame. Original time: {_offsetSnapshot.clipboard.time}, current time: {animationEditContext.clipTime}");
                return;
            }

            if (_usePatternSelection)
            {
                operations.Offset().Apply(_offsetSnapshot, _patternSelectedKeyframes, _offsetModeJSON.val);
            }
            else
            {
                operations.Offset().Apply(_offsetSnapshot, _startJSON.val, _endJSON.val, _offsetModeJSON.val);
            }

            animationEditContext.Sample();
        }

        #endregion

        public void OnTargetsSelectionChanged()
        {
            UpdatePatternSelectionSliderRanges();
            if (_usePatternSelection)
                ApplyPatternSelection();
            SelectionModified();
        }

        protected override void OnCurrentAnimationChanged(AtomAnimationEditContext.CurrentAnimationChangedEventArgs args)
        {
            base.OnCurrentAnimationChanged(args);

            _startJSON.max = current.animationLength;
            _endJSON.max = current.animationLength;

            if (current.animationLength < _startJSON.valNoCallback)
            {
                _startJSON.valNoCallback = 0;
            }

            if (current.animationLength < _endJSON.valNoCallback)
            {
                _endJSON.valNoCallback = current.animationLength;
                if (_startJSON.valNoCallback > _endJSON.valNoCallback) _startJSON.valNoCallback = _endJSON.valNoCallback;
            }

            UpdatePatternSelectionSliderRanges();
            if (_usePatternSelection)
                ApplyPatternSelection();
            SelectionModified();
        }

        private void UpdatePatternSelectionSliderRanges()
        {
            var maxKeyframes = GetMaxKeyframeCount();
            if (_patternIntervalJSON != null)
            {
                _patternIntervalJSON.max = Mathf.Max(2f, maxKeyframes - 1);
                if (_patternIntervalJSON.val > _patternIntervalJSON.max)
                    _patternIntervalJSON.valNoCallback = _patternIntervalJSON.max;
            }
            if (_patternOffsetJSON != null)
            {
                _patternOffsetJSON.max = Mathf.Max(0f, maxKeyframes - 1);
                if (_patternOffsetJSON.val > _patternOffsetJSON.max)
                    _patternOffsetJSON.valNoCallback = _patternOffsetJSON.max;
            }
        }

        public override void OnDestroy()
        {
            base.OnDestroy();

            animationEditContext.animation.animatables.onTargetsSelectionChanged.RemoveListener(OnTargetsSelectionChanged);
        }
    }
}

