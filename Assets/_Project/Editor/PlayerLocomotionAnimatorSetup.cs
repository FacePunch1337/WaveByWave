using System;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace WaveByWave.Editor
{
    [InitializeOnLoad]
    internal static class PlayerLocomotionAnimatorSetup
    {
        private const string ControllerPath = "Assets/_Project/Generated/Animations/Player.controller";
        private const string SchemaMarker = "WaveByWave.PlayerLocomotion:3";

        static PlayerLocomotionAnimatorSetup()
        {
            EditorApplication.delayCall += EnsureController;
        }

        [MenuItem("Tools/Wave by Wave/Rebuild Player Locomotion Animator")]
        private static void RebuildFromMenu()
        {
            RebuildController();
            Debug.Log("[Player] Locomotion Animator rebuilt from the Pirates animation clips.");
        }

        private static void EnsureController()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            var importer = AssetImporter.GetAtPath(ControllerPath);
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (controller != null && importer != null && importer.userData == SchemaMarker &&
                HasParameter(controller, "MoveX") && HasParameter(controller, "Swimming") &&
                HasParameter(controller, "LocomotionRate"))
                return;
            RebuildController();
        }

        private static void RebuildController()
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (controller == null)
                throw new InvalidOperationException($"Missing Animator Controller at '{ControllerPath}'.");

            var layers = controller.layers;
            if (layers.Length == 0)
                controller.AddLayer("Base Layer");
            layers = controller.layers;
            if (layers.Length > 1)
            {
                Array.Resize(ref layers, 1);
                controller.layers = layers;
            }

            var stateMachine = controller.layers[0].stateMachine;
            foreach (var transition in stateMachine.anyStateTransitions)
                stateMachine.RemoveAnyStateTransition(transition);
            foreach (var child in stateMachine.states)
                stateMachine.RemoveState(child.state);
            foreach (var child in stateMachine.stateMachines)
                stateMachine.RemoveStateMachine(child.stateMachine);
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(ControllerPath))
                if (asset is BlendTree)
                    UnityEngine.Object.DestroyImmediate(asset, true);

            for (var i = controller.parameters.Length - 1; i >= 0; i--)
                controller.RemoveParameter(i);
            controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
            controller.AddParameter("MoveX", AnimatorControllerParameterType.Float);
            controller.AddParameter("MoveY", AnimatorControllerParameterType.Float);
            controller.AddParameter("Grounded", AnimatorControllerParameterType.Bool);
            controller.AddParameter("VerticalSpeed", AnimatorControllerParameterType.Float);
            controller.AddParameter("Sprinting", AnimatorControllerParameterType.Bool);
            controller.AddParameter("Swimming", AnimatorControllerParameterType.Bool);
            controller.AddParameter("SwimForward", AnimatorControllerParameterType.Float);
            controller.AddParameter("LocomotionRate", AnimatorControllerParameterType.Float);
            controller.AddParameter("Action", AnimatorControllerParameterType.Trigger);

            var idle = LoadClip("pirate_idle_01.FBX");
            var walkForward = LoadClip("pirate_walk_forward.FBX");
            var walkBack = LoadClip("pirate_walk_back.FBX");
            var walkLeft = LoadClip("pirate_walk_left.FBX");
            var walkRight = LoadClip("pirate_walk_right.FBX");
            var run = LoadClip("pirate_run_forward.FBX");
            var jump = LoadClip("pirate_jump.FBX");
            var swimIdle = LoadClip("pirate_swim_idle.FBX");
            var swimForward = LoadClip("pirate_swim.FBX");

            var groundBlend = new BlendTree
            {
                name = "Ground Directional Blend",
                blendType = BlendTreeType.FreeformCartesian2D,
                blendParameter = "MoveX",
                blendParameterY = "MoveY",
                useAutomaticThresholds = false
            };
            AssetDatabase.AddObjectToAsset(groundBlend, controller);
            groundBlend.AddChild(idle, Vector2.zero);
            groundBlend.AddChild(walkForward, Vector2.up);
            groundBlend.AddChild(walkBack, Vector2.down);
            groundBlend.AddChild(walkLeft, Vector2.left);
            groundBlend.AddChild(walkRight, Vector2.right);

            var swimBlend = new BlendTree
            {
                name = "Swimming Blend",
                blendType = BlendTreeType.Simple1D,
                blendParameter = "SwimForward",
                useAutomaticThresholds = false
            };
            AssetDatabase.AddObjectToAsset(swimBlend, controller);
            swimBlend.AddChild(swimIdle, 0f);
            swimBlend.AddChild(swimForward, 1f);

            var groundState = stateMachine.AddState("Ground locomotion", new Vector3(260f, 20f));
            groundState.motion = groundBlend;
            groundState.speedParameterActive = true;
            groundState.speedParameter = "LocomotionRate";
            var sprintState = stateMachine.AddState("Sprint", new Vector3(520f, -70f));
            sprintState.motion = run;
            sprintState.speedParameterActive = true;
            sprintState.speedParameter = "LocomotionRate";
            var jumpState = stateMachine.AddState("Jump", new Vector3(520f, 110f));
            jumpState.motion = jump;
            jumpState.speed = 1.12f;
            var swimState = stateMachine.AddState("Swimming", new Vector3(770f, 20f));
            swimState.motion = swimBlend;
            swimState.speedParameterActive = true;
            swimState.speedParameter = "LocomotionRate";
            stateMachine.defaultState = groundState;

            AddTransition(groundState, swimState, ("Swimming", AnimatorConditionMode.If));
            AddTransition(groundState, jumpState, 0.02f,
                ("Swimming", AnimatorConditionMode.IfNot), ("Grounded", AnimatorConditionMode.IfNot));
            AddTransition(groundState, sprintState,
                ("Swimming", AnimatorConditionMode.IfNot), ("Grounded", AnimatorConditionMode.If),
                ("Sprinting", AnimatorConditionMode.If));

            AddTransition(sprintState, swimState, ("Swimming", AnimatorConditionMode.If));
            AddTransition(sprintState, jumpState, 0.02f,
                ("Swimming", AnimatorConditionMode.IfNot), ("Grounded", AnimatorConditionMode.IfNot));
            AddTransition(sprintState, groundState,
                ("Swimming", AnimatorConditionMode.IfNot), ("Grounded", AnimatorConditionMode.If),
                ("Sprinting", AnimatorConditionMode.IfNot));

            AddTransition(jumpState, swimState, ("Swimming", AnimatorConditionMode.If));
            AddTransition(jumpState, groundState,
                ("Swimming", AnimatorConditionMode.IfNot), ("Grounded", AnimatorConditionMode.If));

            AddTransition(swimState, groundState,
                ("Swimming", AnimatorConditionMode.IfNot), ("Grounded", AnimatorConditionMode.If));
            AddTransition(swimState, jumpState, 0.02f,
                ("Swimming", AnimatorConditionMode.IfNot), ("Grounded", AnimatorConditionMode.IfNot));

            var importer = AssetImporter.GetAtPath(ControllerPath);
            if (importer != null)
                importer.userData = SchemaMarker;
            EditorUtility.SetDirty(controller);
            EditorUtility.SetDirty(stateMachine);
            EditorUtility.SetDirty(groundBlend);
            EditorUtility.SetDirty(swimBlend);
            AssetDatabase.SaveAssets();
            AssetDatabase.WriteImportSettingsIfDirty(ControllerPath);
        }

        private static void AddTransition(AnimatorState source, AnimatorState destination,
            params (string Parameter, AnimatorConditionMode Mode)[] conditions)
        {
            AddTransition(source, destination, 0.1f, conditions);
        }

        private static void AddTransition(AnimatorState source, AnimatorState destination, float duration,
            params (string Parameter, AnimatorConditionMode Mode)[] conditions)
        {
            var transition = source.AddTransition(destination);
            transition.hasExitTime = false;
            transition.hasFixedDuration = true;
            transition.duration = duration;
            transition.canTransitionToSelf = false;
            transition.interruptionSource = TransitionInterruptionSource.SourceThenDestination;
            foreach (var condition in conditions)
                transition.AddCondition(condition.Mode, 0f, condition.Parameter);
        }

        private static AnimationClip LoadClip(string fileName)
        {
            var path = $"Assets/Pirates/Animations/mecanim/{fileName}";
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                if (asset is AnimationClip clip && !clip.name.StartsWith("__preview__", StringComparison.Ordinal))
                    return clip;
            throw new InvalidOperationException($"Pirates animation clip is missing at '{path}'.");
        }

        private static bool HasParameter(AnimatorController controller, string name)
        {
            foreach (var parameter in controller.parameters)
                if (parameter.name == name)
                    return true;
            return false;
        }
    }
}
