using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
#if UNITY_2019_1_OR_NEWER && USE_ADVANCED_GENERIC_MENU
using GenericMenu = XNodeEditor.AdvancedGenericMenu;
#endif

namespace XNodeEditor {
    /// <summary>Fuzzy-search popup for adding nodes to the graph. Opens at mouse position on right-click.</summary>
    public class NodeSearchWindow : EditorWindow {
        private const string SearchControlName = "NodeSearchField";
        private const int MaxResults = 15;
        private const float WindowWidth = 280f;
        private const float ResultRowHeight = 36f;
        private const float SearchFieldHeight = 24f;
        private const float Padding = 4f;

        private struct NodeEntry {
            public Type type;
            public string displayName; // last segment of menu path
            public string fullPath;    // full menu path
            public bool disabled;
        }

        private NodeGraphEditor graphEditor;
        private Vector2 graphPosition;
        private Vector2 screenMousePosition;
        private Type compatibleType;
        private XNode.NodePort.IO direction;

        private string searchQuery = "";
        private List<NodeEntry> allEntries = new List<NodeEntry>();
        private List<NodeEntry> filteredEntries = new List<NodeEntry>();
        private int selectedIndex = 0;
        private Vector2 scrollPos;
        private bool firstFrame = true;

        /// <summary>Open the node search popup at the current mouse position.</summary>
        public static NodeSearchWindow Open(
            NodeGraphEditor graphEditor,
            Vector2 graphPosition,
            Type compatibleType = null,
            XNode.NodePort.IO direction = XNode.NodePort.IO.Input)
        {
            NodeSearchWindow window = CreateInstance<NodeSearchWindow>();
            window.graphEditor = graphEditor;
            window.graphPosition = graphPosition;
            window.compatibleType = compatibleType;
            window.direction = direction;
            window.BuildEntries();
            window.ApplyFilter();

            // Position popup at mouse, sized to show results
            float height = SearchFieldHeight + Padding * 3
                + Mathf.Min(window.filteredEntries.Count, MaxResults) * ResultRowHeight + Padding;
            height = Mathf.Max(height, SearchFieldHeight + Padding * 3 + ResultRowHeight);

            Vector2 screenMouse = GUIUtility.GUIToScreenPoint(Event.current.mousePosition) + new Vector2(0, -height);
            // Vector2 screenMouse = graphPosition;
            Rect rect = new Rect(screenMouse.x, screenMouse.y, WindowWidth, height);
            // Rect rect = new Rect(graphPosition.x, graphPosition.y, WindowWidth, height);

            window.ShowAsDropDown(rect, new Vector2(WindowWidth, height));
            return window;
        }

        private void BuildEntries() {
            allEntries.Clear();

            Type[] types = NodeEditorReflection.nodeTypes;

            if (compatibleType != null && NodeEditorPreferences.GetSettings().createFilter)
                types = NodeEditorUtilities.GetCompatibleNodesTypes(types, compatibleType, direction).ToArray();

            foreach (Type type in types) {
                string path = graphEditor.GetNodeMenuName(type);
                if (string.IsNullOrEmpty(path)) continue;

                bool disabled = false;
                XNode.Node.DisallowMultipleNodesAttribute disallowAttrib;
                if (NodeEditorUtilities.GetAttrib(type, out disallowAttrib)) {
                    int count = NodeEditorWindow.current.graph.nodes.Count(n => n.GetType() == type);
                    if (count >= disallowAttrib.max) disabled = true;
                }

                int lastSlash = path.LastIndexOf('/');
                string displayName = lastSlash >= 0 ? path.Substring(lastSlash + 1) : path;

                allEntries.Add(new NodeEntry {
                    type = type,
                    displayName = displayName,
                    fullPath = path,
                    disabled = disabled
                });
            }
        }

        private void ApplyFilter() {
            string query = searchQuery.Trim();

            if (string.IsNullOrEmpty(query)) {
                // No filter: sort by menu order then name
                filteredEntries = allEntries
                    .OrderBy(e => graphEditor.GetNodeMenuOrder(e.type))
                    .ThenBy(e => e.displayName)
                    .ToList();
                return;
            }

            string q = query.ToLowerInvariant();

            var scored = new List<(NodeEntry entry, int score)>();
            foreach (NodeEntry entry in allEntries) {
                int score = FuzzyScore(entry.displayName, entry.fullPath, q);
                if (score > 0) scored.Add((entry, score));
            }

            filteredEntries = scored
                .OrderByDescending(x => x.score)
                .ThenBy(x => x.entry.displayName)
                .Take(MaxResults)
                .Select(x => x.entry)
                .ToList();

            selectedIndex = Mathf.Clamp(selectedIndex, -1, Mathf.Max(0, filteredEntries.Count - 1));
        }

        private static int FuzzyScore(string displayName, string fullPath, string query) {
            string name = displayName.ToLowerInvariant();
            string path = fullPath.ToLowerInvariant();

            // Exact match on display name
            if (name == query) return 100;
            // Starts with query
            if (name.StartsWith(query)) return 80;
            // Full path starts with query
            if (path.StartsWith(query)) return 75;
            // Contains query as substring in name
            if (name.Contains(query)) return 60;
            // Contains query as substring in full path
            if (path.Contains(query)) return 50;
            // Subsequence match on display name
            if (IsSubsequence(query, name)) return 30;
            // Subsequence match on full path
            if (IsSubsequence(query, path)) return 20;

            return 0;
        }

        private static bool IsSubsequence(string query, string text) {
            int qi = 0;
            for (int ti = 0; ti < text.Length && qi < query.Length; ti++) {
                if (text[ti] == query[qi]) qi++;
            }
            return qi == query.Length;
        }

        private void OnGUI() {
            Event e = Event.current;

            // Handle keyboard navigation before drawing
            if (e.type == EventType.KeyDown) {
                if (e.keyCode == KeyCode.Escape) {
                    Close();
                    e.Use();
                    return;
                }
                if (e.keyCode == KeyCode.DownArrow) {
                    selectedIndex = Mathf.Min(selectedIndex + 1, filteredEntries.Count - 1);
                    ScrollToSelected();
                    e.Use();
                    Repaint();
                }
                if (e.keyCode == KeyCode.UpArrow) {
                    selectedIndex = Mathf.Max(selectedIndex - 1, -1);
                    ScrollToSelected();
                    e.Use();
                    Repaint();
                }
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) {
                    if (selectedIndex == -1) OpenTreeMenu();
                    else SelectEntry(selectedIndex);
                    e.Use();
                    return;
                }
            }

            // Search field
            GUILayout.Space(Padding);
            GUI.SetNextControlName(SearchControlName);
            EditorGUI.BeginChangeCheck();
            string newQuery = EditorGUILayout.TextField(searchQuery, GUILayout.Height(SearchFieldHeight));

            if (firstFrame) {
                firstFrame = false;
                EditorGUI.FocusTextInControl(SearchControlName);
                Repaint();
            }

            if (EditorGUI.EndChangeCheck()) {
                searchQuery = newQuery;
                selectedIndex = 0;
                scrollPos = Vector2.zero;
                ApplyFilter();
                // Resize window to fit results
                ResizeToContent();
                Repaint();
            }

            GUILayout.Space(Padding);

            // Results list (browse row is always first)
            scrollPos = EditorGUILayout.BeginScrollView(scrollPos, GUIStyle.none, GUI.skin.verticalScrollbar);
            DrawBrowseRow();
            for (int i = 0; i < filteredEntries.Count; i++) {
                DrawEntry(i, filteredEntries[i]);
            }
            if (filteredEntries.Count == 0) {
                GUILayout.Label("No matching nodes", EditorStyles.centeredGreyMiniLabel);
            }
            EditorGUILayout.EndScrollView();

            GUILayout.Space(Padding);
        }

        private void DrawBrowseRow() {
            Rect rowRect = EditorGUILayout.GetControlRect(false, ResultRowHeight);

            bool isSelected = selectedIndex == -1;
            if (isSelected)
                EditorGUI.DrawRect(rowRect, new Color(0.2f, 0.5f, 0.9f, 0.3f));
            else
                EditorGUI.DrawRect(rowRect, new Color(0f, 0f, 0f, 0.05f));

            if (Event.current.type == EventType.MouseDown && rowRect.Contains(Event.current.mousePosition)) {
                OpenTreeMenu();
                Event.current.Use();
                return;
            }
            if (Event.current.type == EventType.MouseMove && rowRect.Contains(Event.current.mousePosition)) {
                selectedIndex = -1;
                Repaint();
            }

            Rect nameRect = new Rect(rowRect.x + Padding, rowRect.y + 3,  rowRect.width - Padding * 2, 16);
            Rect pathRect = new Rect(rowRect.x + Padding, rowRect.y + 18, rowRect.width - Padding * 2, 14);

            GUI.Label(nameRect, "Browse all nodes...", EditorStyles.boldLabel);

            GUIStyle subtitleStyle = new GUIStyle(EditorStyles.miniLabel);
            subtitleStyle.normal.textColor = new Color(0.6f, 0.6f, 0.6f);
            GUI.Label(pathRect, "Show hierarchical tree menu", subtitleStyle);
        }

        private void DrawEntry(int index, NodeEntry entry) {
            Rect rowRect = EditorGUILayout.GetControlRect(false, ResultRowHeight);

            bool isSelected = index == selectedIndex;

            // Background
            if (isSelected && !entry.disabled) {
                EditorGUI.DrawRect(rowRect, new Color(0.2f, 0.5f, 0.9f, 0.3f));
            } else if (index % 2 == 0) {
                EditorGUI.DrawRect(rowRect, new Color(0f, 0f, 0f, 0.05f));
            }

            // Click handling
            if (!entry.disabled && Event.current.type == EventType.MouseDown && rowRect.Contains(Event.current.mousePosition)) {
                SelectEntry(index);
                Event.current.Use();
                return;
            }
            if (Event.current.type == EventType.MouseMove && rowRect.Contains(Event.current.mousePosition)) {
                selectedIndex = index;
                Repaint();
            }

            // Label rects
            Rect nameRect = new Rect(rowRect.x + Padding, rowRect.y + 3, rowRect.width - Padding * 2, 16);
            Rect pathRect = new Rect(rowRect.x + Padding, rowRect.y + 18, rowRect.width - Padding * 2, 14);

            // Draw name
            GUIStyle nameStyle = new GUIStyle(EditorStyles.boldLabel);
            if (entry.disabled) nameStyle.normal.textColor = Color.gray;
            GUI.Label(nameRect, entry.displayName, nameStyle);

            // Draw category path (only if there is one)
            if (entry.fullPath.Contains('/')) {
                string category = entry.fullPath.Substring(0, entry.fullPath.LastIndexOf('/'));
                GUIStyle pathStyle = new GUIStyle(EditorStyles.miniLabel);
                pathStyle.normal.textColor = entry.disabled ? Color.gray : new Color(0.6f, 0.6f, 0.6f);
                GUI.Label(pathRect, category, pathStyle);
            }
        }

        private void SelectEntry(int index) {
            if (index < 0 || index >= filteredEntries.Count) return;
            NodeEntry entry = filteredEntries[index];
            if (entry.disabled) return;

            Close();

            XNode.Node node = graphEditor.CreateNode(entry.type, graphPosition);
            if (node != null) NodeEditorWindow.current.AutoConnect(node);
        }

        private void OpenTreeMenu() {
            var menu = new GenericMenu();
            graphEditor.AddContextMenuItems(menu, graphPosition, compatibleType, direction);
            menu.DropDown(new Rect(screenMousePosition, Vector2.zero));
            // OnLostFocus fires when the dropdown takes focus, closing this window automatically
        }

        private void ScrollToSelected() {
            if (selectedIndex < 0) return;
            float targetY = selectedIndex * ResultRowHeight - ResultRowHeight;
            scrollPos.y = Mathf.Clamp(scrollPos.y, targetY, targetY + ResultRowHeight);
        }

        private void ResizeToContent() {
            int visible = Mathf.Clamp(filteredEntries.Count, 1, MaxResults);
            // float height = SearchFieldHeight + Padding * 3 + visible * ResultRowHeight + Padding;
            float height = SearchFieldHeight + Padding * 3 + ResultRowHeight + visible * ResultRowHeight + Padding;
            minSize = new Vector2(WindowWidth, height);
            maxSize = new Vector2(WindowWidth, height);
        }

        private void OnLostFocus() {
            Close();
        }
    }
}
