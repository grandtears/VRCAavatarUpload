using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.Core;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3A.Editor;
using VRC.SDKBase.Editor;
using VRC.SDKBase.Editor.Api;

namespace LastMemories.AvatarUploader
{
    /// <summary>
    /// シーン内のアバターを登録順にビルド・アップロードするEditor専用ウィンドウ。
    /// 既存のBlueprint IDを使って更新し、項目ごとに結果を記録する。
    /// </summary>
    public sealed class AvatarUploaderWindow : EditorWindow
    {
        // Waiting: 未処理、Running: 処理中、Success: 成功通知あり、Failed: 検証・ビルド・送信などで失敗。
        private enum Status { Waiting, Running, Success, Failed }

        // ウィンドウに表示する1行分のデータ。Unityが保存できるようシリアライズ対象にする。
        [Serializable]
        private sealed class Entry
        {
            // Avatar Descriptorが直接付いているシーン上のGameObject。
            public GameObject root;
            // 表示するマークと再試行対象の判定に使用する。
            public Status status;
            // 成功時はBlueprint ID、失敗時は原因を表示する。
            public string message;
        }

        // リストはUnityのウィンドウ状態に含める。実行中フラグなどは保存しない。
        [SerializeField] private List<Entry> entries = new List<Entry>();
        private Vector2 scroll;
        // 非同期処理の多重起動と、実行中のリスト変更を防ぐ。
        private bool running;
        // SDKの処理を途中で打ち切らず、次の項目へ進む前に停止するための予約。
        private bool stopRequested;
        // ウィンドウ終了後に次の処理や再描画を行わないためのフラグ。
        private bool closing;
        // SDKの進捗率ではなく、現在処理しているアバター名を保持する。
        private string progress;

        // Unity上部のToolsメニューから開く。同じ種類のウィンドウがあれば再利用する。
        [MenuItem("Tools/LastMemories/Avatar Uploader")]
        public static void Open() => GetWindow<AvatarUploaderWindow>("Avatar Uploader");

        private void OnEnable()
        {
            closing = false;
            minSize = new Vector2(460, 300);
            // 再読み込み後は以前の非同期処理を再開できないため、処理中表示を失敗に置き換える。
            // 実際には送信済みの場合もあるので、ユーザーにSDK側の結果確認を促す。
            foreach (var entry in entries)
                if (entry.status == Status.Running)
                {
                    entry.status = Status.Failed;
                    entry.message = "処理が中断されました。SDKでアップロード結果を確認してください。";
                }
        }

        private void OnDisable()
        {
            // 閉じた時点ではSDK処理を強制中断しない。
            // 実行中のawaitが戻った後に終了し、Runのfinallyでチェック状態を復元する。
            closing = true;
            stopRequested = true;
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox("アバタールートを下の枠へドロップしてください。登録順に現在のビルド対象へアップロードします。\n無効なアバタールートは処理前に有効化し、全処理終了・停止時に元のチェック状態へ戻します。\nBlueprint ID設定済みのアバターが対象です。名前・公開設定・サムネイルは既存の内容を維持します。", MessageType.Info);
            // 処理中の追加・削除を禁止し、実行対象と表示リストの対応を保つ。
            using (new EditorGUI.DisabledScope(running))
            {
                var drop = GUILayoutUtility.GetRect(0, 48, GUILayout.ExpandWidth(true));
                GUI.Box(drop, "ここにアバタールートをドラッグ＆ドロップ（複数可）");
                HandleDrop(drop);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("選択中を追加"))
                        foreach (var root in Selection.gameObjects) Add(root);
                    if (GUILayout.Button("成功項目を削除")) entries.RemoveAll(e => e.status == Status.Success);
                    if (GUILayout.Button("リストをクリア")) entries.Clear();
                }
            }

            // 件数が増えても開始・停止ボタンを操作できるよう、一覧部分だけスクロールする。
            scroll = EditorGUILayout.BeginScrollView(scroll);
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        var label = entry.status == Status.Success ? "✓ 成功" : entry.status == Status.Failed ? "✕ 失敗" : entry.status == Status.Running ? "… 処理中" : "待機";
                        var color = GUI.contentColor;
                        // 状態ラベルだけ色を変更し、以降のObjectFieldなどに色を持ち越さない。
                        GUI.contentColor = entry.status == Status.Success ? Color.green : entry.status == Status.Failed ? new Color(1, 0.45f, 0.4f) : color;
                        GUILayout.Label(label, GUILayout.Width(72));
                        GUI.contentColor = color;
                        // 登録後の参照差し替えは許可せず、追加時の検証を必ず通す。
                        using (new EditorGUI.DisabledScope(true)) EditorGUILayout.ObjectField(entry.root, typeof(GameObject), true);
                        using (new EditorGUI.DisabledScope(running))
                            // 削除で次の要素が同じ位置に詰まるので、インデックスを戻して描画漏れを防ぐ。
                            if (GUILayout.Button("削除", GUILayout.Width(45))) { entries.RemoveAt(i--); }
                    }
                    if (!string.IsNullOrEmpty(entry.message)) EditorGUILayout.LabelField(entry.message, EditorStyles.wordWrappedMiniLabel);
                }
            }
            EditorGUILayout.EndScrollView();
            if (running) EditorGUILayout.LabelField(progress ?? "処理中…", EditorStyles.wordWrappedLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                // コンパイル中・Play移行中・Play中は、Editorのビルド処理を開始させない。
                using (new EditorGUI.DisabledScope(running || entries.Count == 0 || EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode))
                {
                    if (GUILayout.Button("連続アップロード（全項目）")) Run(false);
                    if (GUILayout.Button("失敗項目を再試行")) Run(true);
                }
                using (new EditorGUI.DisabledScope(!running || stopRequested))
                    if (GUILayout.Button("現在の処理後に停止")) stopRequested = true;
            }
        }

        private void HandleDrop(Rect rect)
        {
            var evt = Event.current;
            // ドロップ枠内のドラッグ操作だけを受け付け、通常の描画イベントは処理しない。
            if (!rect.Contains(evt.mousePosition) || (evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform)) return;
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (evt.type == EventType.DragPerform)
            {
                // ホバー中は登録しない。ドロップが確定した時点で複数の参照を順に追加する。
                DragAndDrop.AcceptDrag();
                foreach (var obj in DragAndDrop.objectReferences)
                    // GameObjectとComponentのどちらを渡されても、GameObjectにそろえて検証する。
                    Add(obj is GameObject go ? go : (obj as Component)?.gameObject);
            }
            // 処理済みのドラッグイベントが他のUIにも伝わることを防ぐ。
            evt.Use();
        }

        private void Add(GameObject root)
        {
            // Project内のPrefabアセットや子オブジェクトは登録しない。
            // GetComponentは直下だけを確認するため、Descriptor付きのルートを指定する必要がある。
            if (root == null || EditorUtility.IsPersistent(root) || !root.scene.IsValid() || !root.TryGetComponent<VRCAvatarDescriptor>(out _))
            {
                ShowNotification(new GUIContent("シーン内のAvatar Descriptor付きルートを指定してください。"));
                return;
            }
            // 同じシーンオブジェクトの重複登録による二重アップロードを防ぐ。
            if (entries.Exists(e => e.root == root)) return;
            entries.Add(new Entry { root = root });
        }

        /// <summary>
        /// failedOnlyがtrueなら失敗項目のみ、falseなら成功済みを含む全項目を処理する。
        /// GUIから起動する非同期処理の入口。各項目の例外は記録して次へ進む。
        /// </summary>
        private async void Run(bool failedOnly)
        {
            if (running) return;
            // SDK標準の公開Builder APIを取得する。SDKのログイン処理はこのツールでは代行しない。
            if (!VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder))
            {
                EditorUtility.DisplayDialog("Avatar Uploader", "VRChat SDKのControl Panelを開き、ログインしてBuilderを表示してください。", "OK");
                return;
            }
            // SDK側ですでに処理が走っている場合は競合を避けるため開始しない。
            if (builder.BuildState == SdkBuildState.Building || builder.UploadState == SdkUploadState.Uploading)
            {
                ShowNotification(new GUIContent("SDKの処理が完了してから開始してください。"));
                return;
            }
            // 状態を待機へ戻す前に、今回の対象を登録順の別リストとして確定する。
            var queue = entries.FindAll(e => !failedOnly || e.status == Status.Failed);
            if (queue.Count == 0) return;

            // Inspectorのチェック状態（activeSelf）を、変更前に全対象分保存する。
            // activeInHierarchyは親の状態も含むため、復元する値には使用しない。
            var originalActiveStates = new Dictionary<GameObject, bool>();
            foreach (var entry in queue)
                if (entry.root != null && !originalActiveStates.ContainsKey(entry.root))
                    originalActiveStates.Add(entry.root, entry.root.activeSelf);

            running = true;
            stopRequested = false;
            // 再試行時に前回の成功・失敗表示やエラー文が残らないよう初期化する。
            foreach (var entry in queue) { entry.status = Status.Waiting; entry.message = null; }
            // 実行途中のアセンブリ再読み込みで、非同期処理と復元情報が失われることを防ぐ。
            // 終了経路では必ず対応するUnlockReloadAssembliesを呼ぶ。
            EditorApplication.LockReloadAssemblies();
            try
            {
                foreach (var entry in queue)
                {
                    // 停止要求後は次の項目を開始しない。未処理の項目は待機のまま残す。
                    if (stopRequested || closing) break;
                    entry.status = Status.Running;
                    progress = entry.root != null ? entry.root.name : "Missing avatar";
                    Repaint();
                    // Taskが正常終了しただけでは成功にしない。SDKの成功通知も別途確認する。
                    // 各項目ごとに初期化し、前の項目の通知結果を引き継がない。
                    var uploaded = false;
                    string sdkError = null;
                    EventHandler<string> success = (sender, id) => uploaded = true;
                    EventHandler<string> error = (sender, message) => sdkError = message;
                    builder.OnSdkUploadSuccess += success;
                    builder.OnSdkUploadError += error;
                    try
                    {
                        // 登録後に削除されたりDescriptorを外されたりする可能性があるため再検証する。
                        if (entry.root == null || !entry.root.TryGetComponent<VRCAvatarDescriptor>(out _))
                            throw new InvalidOperationException("アバタールートが見つかりません。");
                        // 親の有効化は他のオブジェクトにも影響するため、ルートだけを操作する。
                        if (entry.root.transform.parent != null && !entry.root.transform.parent.gameObject.activeInHierarchy)
                            throw new InvalidOperationException("親オブジェクトが無効です。親を有効にしてから再試行してください。");
                        // 新規登録には名前やサムネイルなどの準備が必要なため、既存IDの更新に限定する。
                        if (!entry.root.TryGetComponent<PipelineManager>(out var pipeline) || string.IsNullOrWhiteSpace(pipeline.blueprintId))
                            throw new InvalidOperationException("Blueprint IDが未設定です。SDKで初回アップロードを行ってください。");
                        // サーバーから最新データを取得し、既存の名前・公開設定などを引き継ぐ。
                        var avatar = await VRCApi.GetAvatar(pipeline.blueprintId, true);
                        // 通信待ちの間にウィンドウが閉じられていたら、ビルドを新たに開始しない。
                        if (closing) throw new InvalidOperationException("ウィンドウが閉じられたため中断しました。");

                        // 順番が来たアバターをビルド直前に有効化する。復元は全処理終了時に行う。
                        entry.root.SetActive(true);
                        // awaitで完了を待ってから次へ進むため、複数アバターの処理は並列にならない。
                        await builder.BuildAndUpload(entry.root, avatar);
                        // SDKが例外を投げずに終了した場合も、通知がなければ成功表示を避ける。
                        if (!uploaded || sdkError != null) throw new InvalidOperationException(sdkError ?? "SDKからアップロード成功通知を受信できませんでした。");
                        entry.status = Status.Success;
                        entry.message = "アップロード完了: " + pipeline.blueprintId;
                    }
                    catch (Exception ex)
                    {
                        // この項目だけを失敗にする。リストには要点、Consoleには例外の詳細を残す。
                        entry.status = Status.Failed;
                        entry.message = ex.Message;
                        Debug.LogError("[Avatar Uploader] " + (entry.root != null ? entry.root.name : "Missing") + ": " + ex);
                    }
                    finally
                    {
                        // 成否にかかわらず購読を解除し、次の項目への通知混入や参照の残留を防ぐ。
                        builder.OnSdkUploadSuccess -= success;
                        builder.OnSdkUploadError -= error;
                    }
                    if (!closing) Repaint();
                }
            }
            finally
            {
                // 成功・失敗・停止・ウィンドウ終了のいずれでも元のチェック状態へ戻す。
                // 一つの復元に失敗しても、残りの復元とアセンブリロック解除を続ける。
                foreach (var state in originalActiveStates)
                {
                    // 処理中に削除されたGameObjectは復元できないため飛ばす。
                    if (state.Key == null) continue;
                    try
                    {
                        // 開始時と異なる場合だけ戻す。対象ルート以外や子のチェック状態は操作しない。
                        if (state.Key.activeSelf != state.Value) state.Key.SetActive(state.Value);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError("[Avatar Uploader] チェック状態の復元に失敗しました: " + ex);
                    }
                }
                running = false;
                progress = null;
                // 正常終了・停止・例外のいずれでも再読み込みを許可する状態に戻す。
                EditorApplication.UnlockReloadAssemblies();
                if (!closing) Repaint();
            }
        }
    }
}
