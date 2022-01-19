using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StoryGenerator;
using StoryGenerator.HomeAnnotation;
using StoryGenerator.Recording;
using StoryGenerator.RoomProperties;
using StoryGenerator.Scripts;
using StoryGenerator.Utilities;
using Unity.Profiling;
using UnityEngine;

[RequireComponent(typeof(Recorder))]
public class UserModel : Driver
{
    
    static ProfilerMarker s_UpdateGraph = new ProfilerMarker("MySystem.UpdateGraph");
    
    private List<CharacterControl> characters = new List<CharacterControl>();
    private List<ScriptExecutor> sExecutors = new List<ScriptExecutor>();
    private List<Recorder> recorders = new List<Recorder>();
    private int numCharacters = 0;
    List<Camera> cameras;
    
    EnvironmentGraphCreator currentGraphCreator = null;
    EnvironmentGraph currentGraph = null;

    public String[] lines =
    {
        "<char0> [walk] <fridge> (1)",
        "<char0> [open] <fridge> (1)",
        "<char0> [walk] <stove> (1)",
        "<char0> [walk] <fridge> (1)"
    };
    
    public class ExecutionConfig : RecorderConfig
    {
        public bool find_solution = true;
        public bool randomize_execution = false;
        public int processing_time_limit = 10;
        public bool recording = false;
        public bool skip_execution = false;
        public bool skip_animation = false;
    }
    
    // Start is called before the first frame update
    void Start()
    {
        CurrentStateList = new List<State>();
        characters = new List<CharacterControl>();
        sExecutors = new List<ScriptExecutor>();
        
        ProcessHome(false);

        InitRooms();

        CameraExpander.ResetCameraExpander();
        
        cameras = new List<Camera>();
        
        if (dataProviders == null) {
            dataProviders = new DataProviders();
        }
        
        List<string> list_assets = dataProviders.AssetsProvider.GetAssetsPaths();
        
        OneTimeInitializer cameraInitializer = new OneTimeInitializer();

        List<GameObject> sceneCharacters = ScriptUtils.FindAllCharacters(transform);
        CharacterControl cc = sceneCharacters[0].GetComponent<CharacterControl>();
        characters.Add(cc);
        CurrentStateList.Add(null);
        numCharacters++;
        cameraInitializer.initialized = false;
        
        List<GameObject> rooms = ScriptUtils.FindAllRooms(transform);
        foreach (GameObject r in rooms)
        {
            if (r.GetComponent<Properties_room>() == null)
                r.AddComponent<Properties_room>();
        }
        
        
        currentGraphCreator = new EnvironmentGraphCreator(dataProviders);
        currentGraph = currentGraphCreator.CreateGraph(transform);
        
        CameraExpander.ResetCameraExpander();
        List<Camera> charCameras = CameraExpander.AddCharacterCameras(sceneCharacters[0], transform, CameraExpander.INT_FORWARD_VIEW_CAMERA_NAME);
        // CameraUtils.DeactivateCameras(charCameras);
        cameras.AddRange(charCameras);
        CameraUtils.InitCameras(cameras);

        StartCoroutine(ProcessScript());
    }

    // Update is called once per frame
    void Update()
    {
        
    }

    IEnumerator ProcessScript()
    {
        ExecutionConfig executionConfig = new ExecutionConfig();
        
        IObjectSelectorProvider objectSelectorProvider = new ObjectSelectionProvider(dataProviders.NameEquivalenceProvider);
        IList<GameObject> objectList = ScriptUtils.FindAllObjects(transform);
        if (recorders.Count != numCharacters)
        {
            createRecorders(executionConfig);
        }
        
        if (sExecutors.Count != numCharacters)
        {
            sExecutors = InitScriptExecutors(executionConfig, objectSelectorProvider, objectList);
        }
        
        List<string> scriptLines = lines.ToList();
        
        for (int i = 0; i < numCharacters; i++)
        {
            sExecutors[i].ClearScript();
            sExecutors[i].smooth_walk = !executionConfig.skip_animation;
        }

        ScriptReader.ParseScript(sExecutors, scriptLines, dataProviders.ActionEquivalenceProvider);
        
        
        List<Tuple<int, Tuple<String, String>>> errorMessages = new List<Tuple<int, Tuple<String, String>>>();
        if (!executionConfig.find_solution)
            errorMessages = ScriptChecker.SolveConflicts(sExecutors);

        // foreach (var script in sExecutors[0].script)
        // {
        //     Debug.Log(script);
        // }
         
        for (int i = 0; i < numCharacters; i++)
        {
            StartCoroutine(sExecutors[i].ProcessAndExecute(false, this));
        }

        while (finishedChars != numCharacters)
        {
            yield return new WaitForSeconds(0.01f);
        }
        
        for (int errorIndex = 0; errorIndex < errorMessages.Count; errorIndex++)
        {
            sExecutors[errorMessages[errorIndex].Item1].report.AddItem(errorMessages[errorIndex].Item2.Item1, errorMessages[errorIndex].Item2.Item2);
            Debug.Log(errorMessages[errorIndex]);
        }
        
        if (!sExecutors[0].Success)
        {
            String message = "";
            message += $"ScriptExcutor : ";
            message += sExecutors[0].CreateReportString();
            message += "\n";
            Debug.LogWarning(message);
        }

        finishedChars = 0;
        ScriptExecutor.actionsPerLine = new Hashtable();
        ScriptExecutor.currRunlineNo = 0;
        ScriptExecutor.currActionsFinished = 0;
        
        ISet<GameObject> changedObjs = new HashSet<GameObject>();
        IDictionary<Tuple<string, int>, ScriptObjectData> script_object_changed = new Dictionary<Tuple<string, int>, ScriptObjectData>();
        List<ActionObjectData> last_action = new List<ActionObjectData>();
        bool single_action = true;
        for (int char_index = 0; char_index < numCharacters; char_index++)
        {
            State currentState = this.CurrentStateList[char_index];
            GameObject rh = currentState.GetGameObject("RIGHT_HAND_OBJECT");
            GameObject lh = currentState.GetGameObject("LEFT_HAND_OBJECT");
            EnvironmentObject obj1;
            EnvironmentObject obj2;
            currentGraphCreator.objectNodeMap.TryGetValue(characters[char_index].gameObject, out obj1);
            Character character_graph;
            currentGraphCreator.characters.TryGetValue(obj1, out character_graph);

            if (sExecutors[char_index].script.Count > 1)
            {
                single_action = false;
            }
            if (sExecutors[char_index].script.Count == 1)
            {
                // If only one action was executed, we will use that action to update the environment
                // Otherwise, we will update using coordinates
                ScriptPair script = sExecutors[char_index].script[0];
                ActionObjectData object_script = new ActionObjectData(character_graph, script, currentState.scriptObjects);
                last_action.Add(object_script);

            }
            Debug.Assert(character_graph != null);
            if (lh != null)
            {
                currentGraphCreator.objectNodeMap.TryGetValue(lh, out obj2);
                character_graph.grabbed_left = obj2;

            }
            else
            {
                character_graph.grabbed_left = null;
            }
            if (rh != null)
            {
                currentGraphCreator.objectNodeMap.TryGetValue(rh, out obj2);
                character_graph.grabbed_right = obj2;
            }
            else
            {

                character_graph.grabbed_right = null;
            }

            IDictionary<Tuple<string, int>, ScriptObjectData> script_objects_state = currentState.scriptObjects;
            foreach (KeyValuePair<Tuple<string, int>, ScriptObjectData> entry in script_objects_state)
            {
                if (!entry.Value.GameObject.IsRoom())
                {
                    //if (entry.Key.Item1 == "cutleryknife")
                    //{

                    //    //int instance_id = entry.Value.GameObject.GetInstanceID();
                    //}
                    changedObjs.Add(entry.Value.GameObject);
                }

                if (entry.Value.OpenStatus != OpenStatus.UNKNOWN)
                {
                    if (sExecutors[char_index].script.Count > 0 && sExecutors[char_index].script[0].Action.Name.Instance == entry.Key.Item2)
                    {
                        script_object_changed[entry.Key] = entry.Value;
                    }
                }

            }
            foreach (KeyValuePair<Tuple<string, int>, ScriptObjectData> entry in script_object_changed)
            {
                if (entry.Value.OpenStatus == OpenStatus.OPEN)
                {
                    currentGraphCreator.objectNodeMap[entry.Value.GameObject].states.Remove(ObjectState.CLOSED);
                    currentGraphCreator.objectNodeMap[entry.Value.GameObject].states.Add(ObjectState.OPEN);
                }
                else if (entry.Value.OpenStatus == OpenStatus.CLOSED)
                {
                    currentGraphCreator.objectNodeMap[entry.Value.GameObject].states.Remove(ObjectState.OPEN);
                    currentGraphCreator.objectNodeMap[entry.Value.GameObject].states.Add(ObjectState.CLOSED);
                }
            }

            using (s_UpdateGraph.Auto())
            {
                if (single_action)
                    currentGraph = currentGraphCreator.UpdateGraph(transform, null, last_action);
                else
                    currentGraph = currentGraphCreator.UpdateGraph(transform, changedObjs);
            }
        }
        
    }

    private List<ScriptExecutor> InitScriptExecutors(ExecutionConfig config, IObjectSelectorProvider objectSel, IList<GameObject> objectList)
    {
        List<ScriptExecutor> sExecutors = new List<ScriptExecutor>();

        InteractionCache interaction_cache = new InteractionCache();
        for (int i = 0; i < numCharacters; i++)
        {
            CharacterControl chc = characters[i];
            chc.DoorControl.Update(objectList);


            // Initialize the scriptExecutor for the character
            ScriptExecutor sExecutor = new ScriptExecutor(objectList, dataProviders.RoomSelector, objectSel, recorders[i], i, interaction_cache, !config.skip_animation);
            sExecutor.RandomizeExecution = config.randomize_execution;
            sExecutor.ProcessingTimeLimit = config.processing_time_limit;
            sExecutor.SkipExecution = config.skip_execution;
            sExecutor.AutoDoorOpening = false;

            sExecutor.Initialize(chc, recorders[i].CamCtrls);
            sExecutors.Add(sExecutor);
        }
        return sExecutors;
    }
    
    private void updateRecorder(ExecutionConfig config, string outDir, Recorder rec)
        {
            // ICameraControl cameraControl = null;
            rec.Recording = config.recording;
        }
    private void createRecorder(ExecutionConfig config, string outDir, int index)
    {
        Recorder rec = recorders[index];
        updateRecorder(config, outDir, rec);
    }

    private void createRecorders(ExecutionConfig config)
    {
        // For the 1st Recorder.
        recorders.Clear();
        recorders.Add(GetComponent<Recorder>());
        recorders[0].charIdx = 0;
        if (numCharacters > 1)
        {
            for (int i = 1; i < numCharacters; i++)
            {
                recorders.Add(gameObject.AddComponent<Recorder>() as Recorder);
                recorders[i].charIdx = i;
            }
        }

        for (int i = 0; i < numCharacters; i++)
        {
            string outDir = Path.Combine(config.output_folder, config.file_name_prefix, i.ToString());
            Directory.CreateDirectory(outDir);
            createRecorder(config, outDir, i);
        }
    }
    
    private void InitRooms()
    {
        List<GameObject> rooms = ScriptUtils.FindAllRooms(transform);

        foreach (GameObject r in rooms) {
            r.AddComponent<Properties_room>();
        }
    }
    
    void ProcessHome(bool randomizeExecution)
    {
        UtilsAnnotator.ProcessHome(transform, randomizeExecution);

        ColorEncoding.EncodeCurrentScene(transform);
        // Disable must come after color encoding. Otherwise, GetComponent<Renderer> failes to get
        // Renderer for the disabled objects.
        UtilsAnnotator.PostColorEncoding_DisableGameObjects();
    }
}
