using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using ESDP.Models;
using Microsoft.Win32;

using Point = System.Windows.Point;
using Path = System.IO.Path;

namespace ESDP
{
    public partial class MainWindow
    {
        private sealed class HistoryState
        {
            public Project Project { get; set; } = new();
            public int CurrentSheetIndex { get; set; }
        }

        private HistorySnapshot CaptureHistoryState()
        {
            CaptureCurrentSheet();
            var state = new HistoryState
            {
                Project = new Project
                {
                    SchemaVersion = Project.CurrentSchemaVersion,
                    Devices = _registeredDevices.Select(item => item.Device).ToList(),
                    Cables = _registeredCables.Select(item => item.Cable).ToList(),
                    CustomDeviceKinds = _customDeviceKinds.ToList(),
                    DeviceCategoryNames = _deviceCategoryNames.ToList(),
                    DeviceCategoryColors = new(_deviceCategoryColors),
                    CustomCableCategories = _customCableCategories.ToList(),
                    Sheets = _sheets.ToList()
                },
                CurrentSheetIndex = _currentSheetIndex
            };
            return _historyStore.Capture(state);
        }

        private void ResetUndoHistory()
        {
            _keyboardNudgeTimer.Stop();
            _keyboardNudgeActive = false;
            _keyboardNudgeStartState = null;
            _undoHistory.Clear();
            _redoHistory.Clear();
            _historyBaseline = null;
            _historyStore.Clear();
            _historyBaseline = CaptureHistoryState();
            _historyReady = true;
        }

        /// <summary>
        /// 드래그 중간 프레임은 기록하지 않고, 마우스를 놓은 뒤 호출되는
        /// 최종 변경만 한 단계로 저장한다.
        /// </summary>
        private bool IsHistoryGestureActive()
            => _adjustingShape != null || _rotatingShape != null || _movingShape != null || _resizingShape != null ||
               _cablePortDragPlaced != null ||
               _cableBranchDragPlaced != null ||
               _cableSegDragPlaced != null ||
               _wholeCableDragAnchor != null ||
               _cableLabelDragPlaced != null ||
               _devicePortLabelDragPlaced != null ||
               _shapeLineEndpointDragPlaced != null ||
               _shapeLineSegmentDragPlaced != null ||
               _portDragPlaced != null ||
               _movingAnchor != null || _resizingDevice != null ||
               _keyboardNudgeActive ||
               _shapeDrawPreview != null ||
               _shapeDrawElbowPreview != null ||
               _shapeDrawPreviewCanvas != null ||
               _zoneDrawPreview != null ||
               _zoneDragPlaced != null;

        private void BeginOrContinueKeyboardNudge(Key direction)
        {
            if (_keyboardNudgeActive &&
                _keyboardNudgeDirection != direction)
                EndKeyboardNudge();

            if (!_keyboardNudgeActive)
            {
                // 키보드 이동 직전까지의 다른 편집을 먼저 확정한 뒤,
                // 이번 연속 이동만의 시작점을 별도로 보관한다.
                CommitHistorySnapshot();
                _keyboardNudgeStartState = CaptureHistoryState();
            }
            _keyboardNudgeActive = true;
            _keyboardNudgeDirection = direction;
            _keyboardNudgeTimer.Stop();
            _keyboardNudgeTimer.Start();
        }

        private void EndKeyboardNudge()
        {
            _keyboardNudgeTimer.Stop();
            if (!_keyboardNudgeActive) return;

            _keyboardNudgeActive = false;
            HistorySnapshot? start = _keyboardNudgeStartState;
            _keyboardNudgeStartState = null;
            if (!_historyReady || _restoringHistory || _applyingSheet ||
                start == null)
                return;

            HistorySnapshot current = CaptureHistoryState();
            if (start.Fingerprint == current.Fingerprint)
                return;

            _undoHistory.Add(start);
            if (_undoHistory.Count > MaximumUndoSteps)
                _undoHistory.RemoveAt(0);
            _historyBaseline = current;
            _redoHistory.Clear();
            TrimHistoryMemory();
        }

        private void CommitHistoryIfActionComplete()
        {
            if (!_historyReady || _restoringHistory ||
                IsHistoryGestureActive())
                return;
            CommitHistorySnapshot();
        }

        private void FlushPendingHistoryCommit()
        {
            EndKeyboardNudge();
            CommitHistorySnapshot();
        }

        private void CommitHistorySnapshot()
        {
            if (!_historyReady || _restoringHistory) return;

            HistorySnapshot current = CaptureHistoryState();
            if (_historyBaseline == null)
            {
                _historyBaseline = current;
                return;
            }
            if (current.Fingerprint == _historyBaseline.Fingerprint)
                return;

            _undoHistory.Add(_historyBaseline);
            if (_undoHistory.Count > MaximumUndoSteps)
                _undoHistory.RemoveAt(0);
            _historyBaseline = current;
            _redoHistory.Clear();
            TrimHistoryMemory();
        }

        private void TrimHistoryMemory()
        {
            IEnumerable<HistorySnapshot> LiveSnapshots()
            {
                foreach (var snapshot in _undoHistory) yield return snapshot;
                foreach (var snapshot in _redoHistory) yield return snapshot;
                if (_historyBaseline != null) yield return _historyBaseline;
                if (_keyboardNudgeStartState != null) yield return _keyboardNudgeStartState;
            }

            _historyStore.Collect(LiveSnapshots());
            long MetadataBytes() => LiveSnapshots().Sum(snapshot =>
                128L + 144L * (snapshot.Chunks.Length + snapshot.Strings.Length));
            // The current state is always retained. A single unusually large image
            // can exceed the budget; in that case all older history is released.
            while (_historyStore.RetainedBytes + MetadataBytes() > MaximumHistoryBytes &&
                   (_undoHistory.Count > 0 || _redoHistory.Count > 0))
            {
                if (_undoHistory.Count > 0) _undoHistory.RemoveAt(0);
                else _redoHistory.RemoveAt(0);
                _historyStore.Collect(LiveSnapshots());
            }
        }

        private void RestoreHistoryState(HistorySnapshot snapshot)
        {
            var state = _historyStore.Restore<HistoryState>(snapshot);

            _keyboardNudgeTimer.Stop();
            _keyboardNudgeActive = false;
            _keyboardNudgeStartState = null;
            _restoringHistory = true;
            try
            {
                foreach (var placed in _placed)
                    MainCanvas.Children.Remove(placed.Container);
                foreach (var placed in _placedCables)
                    MainCanvas.Children.Remove(placed.Container);
                foreach (var placed in _placedShapes)
                    MainCanvas.Children.Remove(placed.Container);
                foreach (var placed in _placedZones)
                    MainCanvas.Children.Remove(placed.Container);
                _crossingOverlay?.Children.Clear();
                _labelOverlay?.Children.Clear();
                _placed.Clear();
                _placedCables.Clear();
                _placedShapes.Clear();
                _placedZones.Clear();
                _selectedSet.Clear();
                _selectedCable = null;
                _selectedShape = null;

                _registeredDevices.Clear();
                foreach (var device in state.Project.Devices)
                    _registeredDevices.Add(new DeviceListItem(device));
                _registeredCables.Clear();
                foreach (var cable in state.Project.Cables)
                    _registeredCables.Add(new CableListItem(cable));
                _customDeviceKinds.Clear();
                _customCableCategories.Clear();
                _customDeviceKinds.AddRange(state.Project.CustomDeviceKinds ?? new List<string>());
                RestoreDeviceCategories(state.Project);
                _customCableCategories.AddRange(state.Project.CustomCableCategories ?? new List<string>());
                ConfigureSidebarViews();

                _sheets.Clear();
                foreach (var sheet in state.Project.Sheets)
                    _sheets.Add(sheet);
                if (_sheets.Count == 0)
                    _sheets.Add(new Sheet
                    {
                        Name = "Sheet1",
                        PaperSize = PaperSize.A3_Landscape
                    });

                int index = Math.Max(
                    0, Math.Min(state.CurrentSheetIndex, _sheets.Count - 1));
                _currentSheetIndex = -1;
                RefreshSheetTabs(index);
                ApplySheet(index);
                _isDirtyValue = true;
            }
            finally
            {
                _restoringHistory = false;
            }
        }

    }
}
