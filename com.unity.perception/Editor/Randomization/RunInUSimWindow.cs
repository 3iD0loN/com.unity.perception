﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Boo.Lang.Runtime;
using Unity.Simulation.Client;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.UIElements;
using UnityEngine.Perception.Randomization.Scenarios;
using UnityEngine.UIElements;
using ZipUtility;

namespace UnityEngine.Perception.Randomization.Editor
{
    public class RunInUSimWindow : EditorWindow
    {
        string m_BuildZipPath;
        SysParamDefinition m_SysParam;
        float m_LastRunStatusPing;

        TextField m_RunNameField;
        TextField m_RunExecutionIdField;
        VisualElement m_RunStatusContainer;
        IntegerField m_TotalIterationsField;
        IntegerField m_InstanceCountField;
        ObjectField m_MainSceneField;
        ObjectField m_ScenarioField;
        Button m_RunButton;

        TextElement m_NumNotRun;
        TextElement m_NumFailures;
        TextElement m_NumInProgress;
        TextElement m_NumSuccess;
        TextElement m_RunState;

        [MenuItem("Window/Run in USim")]
        public static void ShowWindow()
        {
            var window = GetWindow<RunInUSimWindow>();
            window.titleContent = new GUIContent("Run In Unity Simulation");
            window.minSize = new Vector2(250, 50);
            window.Show();
        }

        void OnEnable()
        {
            Project.Activate();
            Project.clientReadyStateChanged += CreateEstablishingConnectionUI;
            CreateEstablishingConnectionUI(Project.projectIdState);
        }

        void OnFocus()
        {
            Application.runInBackground = true;
        }

        void OnLostFocus()
        {
            Application.runInBackground = false;
        }

        void CreateEstablishingConnectionUI(Project.State state)
        {
            rootVisualElement.Clear();
            if (Project.projectIdState == Project.State.Pending)
            {
                var waitingText = new TextElement();
                waitingText.text = "Waiting for connection to Unity Cloud...";
                rootVisualElement.Add(waitingText);
            }
            else if (Project.projectIdState == Project.State.Invalid)
            {
                var waitingText = new TextElement();
                waitingText.text = "The current project must be associated with a valid Unity Cloud project " +
                    "to run in Unity Simulation";
                rootVisualElement.Add(waitingText);
            }
            else
            {
                CreateRunInUSimUI();
            }
        }

        /// <summary>
        /// Enables a visual element to remember values between editor sessions
        /// </summary>
        static void SetViewDataKey(VisualElement element)
        {
            element.viewDataKey = $"RunInUSim_{element.name}";
        }

        void CreateRunInUSimUI()
        {
            var root = rootVisualElement;
            AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
                $"{StaticData.uxmlDir}/RunInUSimWindow.uxml").CloneTree(root);

            m_RunNameField = root.Q<TextField>("run-name");
            SetViewDataKey(m_RunNameField);

            m_TotalIterationsField = root.Q<IntegerField>("total-iterations");
            SetViewDataKey(m_TotalIterationsField);

            m_InstanceCountField = root.Q<IntegerField>("instance-count");
            SetViewDataKey(m_InstanceCountField);

            m_MainSceneField = root.Q<ObjectField>("main-scene");
            m_MainSceneField.objectType = typeof(SceneAsset);

            m_ScenarioField = root.Q<ObjectField>("scenario");
            m_ScenarioField.objectType = typeof(ScenarioBase);

            m_RunStatusContainer = root.Q<VisualElement>("run-status-container");
            m_RunExecutionIdField = root.Q<TextField>("run-execution-id");
            SetViewDataKey(m_RunExecutionIdField);

            m_NumNotRun = root.Q<TextElement>("num-not-run");
            m_NumFailures = root.Q<TextElement>("num-failures");
            m_NumInProgress = root.Q<TextElement>("num-in-progress");
            m_NumSuccess = root.Q<TextElement>("num-success");
            m_RunState = root.Q<TextElement>("run-state");

            var downloadManifestButton = root.Q<Button>("download-manifest");
            downloadManifestButton.clicked += DownloadManifest;

            var sysParamDefinitions = API.GetSysParams();
            var sysParamMenu = root.Q<ToolbarMenu>("sys-param");
            foreach (var definition in sysParamDefinitions)
                sysParamMenu.menu.AppendAction(definition.description, action => m_SysParam = definition);
            sysParamMenu.text = sysParamDefinitions[0].description;
            m_SysParam = sysParamDefinitions[0];

            m_RunButton = root.Q<Button>("run-button");
            m_RunButton.clicked += RunInUSim;
        }

        // void ToggleVisibility(VisualElement element, bool visible)
        // {
        //     Debug.Log(visible);
        //     element.style.display = visible
        //         ? new StyleEnum<DisplayStyle>(DisplayStyle.Flex)
        //         : new StyleEnum<DisplayStyle>(DisplayStyle.None);
        // }

        void OnInspectorUpdate()
        {
            if (!string.IsNullOrEmpty(m_RunExecutionIdField.value) &&
                m_LastRunStatusPing < Time.realtimeSinceStartup - 3f)
            {
                m_LastRunStatusPing = Time.realtimeSinceStartup;
                UpdateRunStatus();
            }
        }

        void UpdateRunStatus()
        {
            var summary = API.Summarize(m_RunExecutionIdField.value);
            Debug.Log(summary.state.code);
            m_NumNotRun.text = summary.num_not_run.ToString();
            m_NumFailures.text = summary.num_failures.ToString();
            m_NumInProgress.text = summary.num_in_progress.ToString();
            m_NumSuccess.text = summary.num_success.ToString();
            m_RunState.text = summary.state.code;
        }

        void DownloadManifest()
        {
            if (!string.IsNullOrEmpty(m_RunExecutionIdField.value))
            {
                var manifest = API.GetManifest(m_RunExecutionIdField.value);
                var manifestFilePath = EditorUtility.SaveFilePanel("Save Manifest", Application.dataPath, "manifest", "csv");
                var lines = new string[manifest.Count + 1];
                lines[0] = "run_execution_id,app_param_id,instance_id,attempt_id,file_name,download_uri";
                var i = 1;
                foreach (var pair in manifest)
                {
                    var e = pair.Value;
                    lines[i++] = $"{e.executionId},{e.appParamId},{e.instanceId},{e.attemptId},{e.fileName},{e.downloadUri}";
                }
                File.WriteAllLines(manifestFilePath, lines);
            }
        }

        async void RunInUSim()
        {
            ValidateSettings();
            CreateLinuxBuildAndZip();
            var run = await StartUSimRun();
            m_RunExecutionIdField.value = run.executionId;
        }

        void ValidateSettings()
        {
            if (string.IsNullOrEmpty(m_RunNameField.value))
                throw new RuntimeException("Invalid run name");
            if (m_ScenarioField.value == null)
                throw new RuntimeException("Null scenario");
            if (m_MainSceneField.value == null)
                throw new RankException("Null main scene");
        }

        void CreateLinuxBuildAndZip()
        {
            // Ensure that scenario serialization is enabled
            var scenario = (USimScenario)m_ScenarioField.value;
            scenario.deserializeOnStart = true;

            // Create build directory
            var pathToProjectBuild = Application.dataPath + "/../" + "Build/";
            if (!Directory.Exists(pathToProjectBuild + m_RunNameField.value))
                Directory.CreateDirectory(pathToProjectBuild + m_RunNameField.value);

            pathToProjectBuild = pathToProjectBuild + m_RunNameField.value + "/";

            // Create Linux build
            Debug.Log("Creating Linux build...");
            var buildPlayerOptions = new BuildPlayerOptions
            {
                scenes = new[] { AssetDatabase.GetAssetPath(m_MainSceneField.value) },
                locationPathName = Path.Combine(pathToProjectBuild, m_RunNameField.value + ".x86_64"),
                target = BuildTarget.StandaloneLinux64
            };
            var report = BuildPipeline.BuildPlayer(buildPlayerOptions);
            var summary = report.summary;
            if (summary.result != BuildResult.Succeeded)
                throw new RuntimeException($"Build did not succeed: status = {summary.result}");
            Debug.Log("Created Linux build");

            // Zip the build
            Debug.Log("Starting to zip...");
            var buildFolder = Application.dataPath + "/../" + "Build/" + m_RunNameField.value;
            Zip.DirectoryContents(buildFolder, m_RunNameField.value);
            m_BuildZipPath = buildFolder + ".zip";
            Debug.Log("Created build zip");
        }

        List<AppParam> GenerateAppParamIds(CancellationToken token)
        {
            var appParamIds = new List<AppParam>();
            for (var i = 0; i < m_InstanceCountField.value; i++)
            {
                if (token.IsCancellationRequested)
                    return null;
                var appParamName = $"{m_RunNameField.value}_{i}";
                var appParamId = API.UploadAppParam(appParamName, new USimConstants
                {
                    totalIterations = m_TotalIterationsField.value,
                    instanceCount = m_InstanceCountField.value,
                    instanceIndex = i
                });
                appParamIds.Add(new AppParam()
                {
                    id = appParamId,
                    name = appParamName,
                    num_instances = 1
                });
            }
            return appParamIds;
        }

        async Task<Run> StartUSimRun()
        {
            m_RunButton.SetEnabled(false);
            var cancellationTokenSource = new CancellationTokenSource();
            var token = cancellationTokenSource.Token;

            Debug.Log("Uploading build...");
            var buildId = await API.UploadBuildAsync(
                m_RunNameField.value,
                m_BuildZipPath,
                cancellationTokenSource: cancellationTokenSource);
            Debug.Log($"Build upload complete: build id {buildId}");

            var appParams = GenerateAppParamIds(token);
            if (token.IsCancellationRequested)
                return null;
            Debug.Log($"Generated app-param ids: {appParams.Count}");

            var runDefinitionId = API.UploadRunDefinition(new RunDefinition
            {
                app_params = appParams.ToArray(),
                name = m_RunNameField.value,
                sys_param_id = m_SysParam.id,
                build_id = buildId
            });
            Debug.Log($"Run definition upload complete: run definition id {runDefinitionId}");

            var run = Run.CreateFromDefinitionId(runDefinitionId);
            run.Execute();
            cancellationTokenSource.Dispose();
            Debug.Log($"Executing run: {run.executionId}");
            return run;
        }
    }
}
