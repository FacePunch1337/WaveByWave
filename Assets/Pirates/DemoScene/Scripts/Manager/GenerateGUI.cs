using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class GenerateGUI : MonoBehaviour {


    public List<ItemsHolder> characters;

    public Transform itemsUIHolder;
    public Transform rightHolder;
    public Transform assetHolder;
    public OptionsHolder optionHodler;

    private PresetCharacter presets;
    private ItemsHolder currentCharacterItems;
    private Animation currentCharacterAnimation;
    private OptionsHolder presetOptions;

    private string currentAnimation;
    private Transform presetsHolder;
    private Transform animationsHolder;
    private List<Transform> assetOptions;

    private int characterCounter;

    private void Start()
    {
        assetOptions = new List<Transform>();
        characterCounter = 0;

        if (characters.Count > 1)
        {
            GenerateCharactersUI();
        }else if(characters.Count == 1)
        {
            presets = characters[characterCounter].gameObject.GetComponent<PresetCharacter>();
            GeneratePresetUI();
            ActivatePlayerCharacter();
        }
    }

    //-----------------------------------------------------------------
    private void GenerateCharactersUI()
    {
        var assetOptionInstance = Instantiate(assetHolder, rightHolder);

        var holderName = assetOptionInstance.GetComponentInChildren<Text>();

        if (holderName != null)
            holderName.text = "Characters";

        var assetOptions = new List<string>();

        foreach (var character in characters)
        {
            assetOptions.Add(character.name);
        }

        var optionsInstance = Instantiate(optionHodler, assetOptionInstance);
        optionsInstance.assetList.AddOptions(assetOptions);
        optionsInstance.assetList.onValueChanged.AddListener(delegate { ActivateCharacter(optionsInstance); });
        optionsInstance.nextButton.onClick.AddListener(delegate { ActivateNextCharacter(optionsInstance); });
        optionsInstance.previousButton.onClick.AddListener(delegate { ActivatePreviousCharacter(optionsInstance); });
        optionsInstance.assetList.value = 0;
        ActivateCharacter(optionsInstance);
    }

    private void ActivateNextCharacter(OptionsHolder optionsHolder)
    {
        if (optionsHolder.assetList.value < optionsHolder.assetList.options.Count)
        {
            optionsHolder.assetList.value++;
            ActivateCharacter(optionsHolder);
        }
    }

    private void ActivatePreviousCharacter(OptionsHolder optionsHolder)
    {
        if (optionsHolder.assetList.value > 0)
        {
            optionsHolder.assetList.value--;
            ActivateCharacter(optionsHolder);
        }
    }

    private void ActivateCharacter(OptionsHolder optionsHolder)
    {
        characters[characterCounter].gameObject.SetActive(false);
        if (presets != null)
            presets.DeactivatePreset();

        characters[optionsHolder.assetList.value].gameObject.SetActive(true);

        presets = characters[optionsHolder.assetList.value].gameObject.GetComponent<PresetCharacter>();
        characterCounter = optionsHolder.assetList.value;

        GeneratePresetUI();
        ActivatePlayerCharacter();

        optionsHolder.CheckButtons();
    }

    //-----------------------------------------------------------------
    private void GenerateItemsUI()
    {
        foreach (var assetOption in assetOptions)
            Destroy(assetOption.gameObject);

        assetOptions.Clear();

        if (currentCharacterItems != null)
        {
            for (int i = 0; i < currentCharacterItems.itemsConfiguration.Count; i++)
            {
                var itemIndex = i;

                var assetHolderInstance = Instantiate(assetHolder, itemsUIHolder);

                var holderName = assetHolderInstance.GetComponentInChildren<Text>();

                if (holderName != null)
                    holderName.text = currentCharacterItems.itemsConfiguration[itemIndex].displayName;

                var optionsInstance = Instantiate(optionHodler, assetHolderInstance);

                var optionsNames = new List<string>();

                foreach (var option in currentCharacterItems.itemsConfiguration[itemIndex].items)
                {
                    if (option.model != null)
                    {
                        optionsNames.Add(option.model.name);
                    }
                    else
                    {
                        optionsNames.Add("None");
                    }
                }

                var materialOptions = new List<Transform>();

                optionsInstance.assetList.AddOptions(optionsNames);
                optionsInstance.assetList.value = currentCharacterItems.itemsConfiguration[itemIndex].GetCounter;
                optionsInstance.assetList.onValueChanged.AddListener(delegate { ActivateModel(itemIndex, optionsInstance, materialOptions, assetHolderInstance); });
                optionsInstance.nextButton.onClick.AddListener(delegate { NextModel(itemIndex, optionsInstance, materialOptions, assetHolderInstance); });
                optionsInstance.previousButton.onClick.AddListener(delegate { PreviousModel(itemIndex, optionsInstance, materialOptions, assetHolderInstance); });
                ActivateModel(itemIndex, optionsInstance, materialOptions, assetHolderInstance);
                assetOptions.Add(assetHolderInstance);
            }
        }
    }

    public void NextModel(int elementIndex, OptionsHolder optionsDropdown, List<Transform> materialOptions, Transform assetHolder)
    {
        if (optionsDropdown.assetList.value < optionsDropdown.assetList.options.Count)
        {
            optionsDropdown.assetList.value++;
            ActivateModel(elementIndex, optionsDropdown, materialOptions, assetHolder);
        }
    }

    public void PreviousModel(int elementIndex, OptionsHolder optionsDropdown, List<Transform> materialOptions, Transform assetHolder)
    {
        if (optionsDropdown.assetList.value > 0)
        {
            optionsDropdown.assetList.value--;
            ActivateModel(elementIndex, optionsDropdown, materialOptions, assetHolder);
        }
    }

    public void ActivateModel(int elementIndex, OptionsHolder optionsHolder, List<Transform> materialOptions, Transform assetHolder)
    {
        //If we used presets our character is currently inactive therefore we need to switch it on
        if(characters[characterCounter].gameObject.activeInHierarchy == false)
        {
            if (presets != null)
                presets.DeactivatePreset();

            characters[characterCounter].gameObject.SetActive(true);
        }

        currentCharacterItems.itemsConfiguration[elementIndex].ActivateModel(optionsHolder.assetList.value);
        var time = currentCharacterAnimation[currentAnimation].time;
        currentCharacterItems.itemsConfiguration[elementIndex].PlayAnimation(currentAnimation, time);
        optionsHolder.CheckButtons();

        //Clearing material options 
        foreach (var materialOption in materialOptions)
            if (materialOption != null)
                Destroy(materialOption.gameObject);

        //Generating material options for given item
        if (currentCharacterItems.itemsConfiguration[elementIndex].items[optionsHolder.assetList.value].materials != null)
        {
            for (int i = 0; i < currentCharacterItems.itemsConfiguration[elementIndex].items[optionsHolder.assetList.value].materials.Count; i++)
            {
                var materialIndex = i;
                var instantiatedMaterialOption = Instantiate(optionHodler, assetHolder);
                materialOptions.Add(instantiatedMaterialOption.transform);

                var optionsNames = new List<string>();

                foreach (var option in currentCharacterItems.itemsConfiguration[elementIndex].items[optionsHolder.assetList.value].materials[i].aviableMaterials)
                {
                    optionsNames.Add(option.name);
                }

                instantiatedMaterialOption.assetList.AddOptions(optionsNames);
                instantiatedMaterialOption.assetList.value = currentCharacterItems.itemsConfiguration[elementIndex].items[optionsHolder.assetList.value].materials[materialIndex].GetCounter;
                instantiatedMaterialOption.assetList.onValueChanged.AddListener(delegate { ChangeMaterial(elementIndex, materialIndex, instantiatedMaterialOption); });
                instantiatedMaterialOption.nextButton.onClick.AddListener(delegate { NextMaterial(elementIndex, materialIndex, instantiatedMaterialOption); });
                instantiatedMaterialOption.previousButton.onClick.AddListener(delegate { PreviousMaterial(elementIndex, materialIndex, instantiatedMaterialOption); });
                ChangeMaterial(elementIndex, materialIndex, instantiatedMaterialOption);
            }
        }
    }

    //-----------------------------------------------------------------
    public void NextMaterial(int meshIndex, int objectIndex, OptionsHolder optionsHolder)
    {
        if (optionsHolder.assetList.value < optionsHolder.assetList.options.Count - 1)
        {
            optionsHolder.assetList.value++;
            ChangeMaterial(meshIndex, objectIndex, optionsHolder);
        }
    }

    public void PreviousMaterial(int meshIndex, int objectIndex, OptionsHolder optionsHolder)
    {
        if (optionsHolder.assetList.value > 0)
        {
            optionsHolder.assetList.value--;
            ChangeMaterial(meshIndex, objectIndex, optionsHolder);
        }
    }

    public void ChangeMaterial(int meshIndex, int objectIndex, OptionsHolder optionsHodler)
    {
        if(characters[characterCounter].gameObject.activeInHierarchy == false)
        {
            if (presets != null)
                presets.DeactivatePreset();

            characters[characterCounter].gameObject.SetActive(true);
        }

        optionsHodler.CheckButtons();
        currentCharacterItems.itemsConfiguration[meshIndex].ChangeMaterial(objectIndex, optionsHodler.assetList.value);
    }

    //-----------------------------------------------------------------
    private void GenerateAnimationsUI()
    {
        if (animationsHolder != null)
            Destroy(animationsHolder.gameObject);

        if (currentCharacterAnimation != null)
        {
            var defaultAnimation = currentCharacterAnimation.clip.name;
            var animationsNames = new List<string>();
            int defaultAnimationIndex = 0;
            int counter = 0;

            foreach (AnimationState state in currentCharacterAnimation)
            {
                animationsNames.Add(state.name);

                if (state.name != currentCharacterAnimation.clip.name)
                    counter++;
                else
                    defaultAnimationIndex = counter;
            }

            var assetHolderInstance = Instantiate(assetHolder, rightHolder);

            var holderName = assetHolderInstance.GetComponentInChildren<Text>();

            if (holderName != null)
                holderName.text = "Animations";

            var optionsInstance = Instantiate(optionHodler, assetHolderInstance);

            optionsInstance.assetList.AddOptions(animationsNames);
            optionsInstance.assetList.value = defaultAnimationIndex;
            optionsInstance.assetList.onValueChanged.AddListener(delegate { PlayAnimation(optionsInstance); });
            optionsInstance.nextButton.onClick.AddListener(delegate { NextPlayAnimation(optionsInstance); });
            optionsInstance.previousButton.onClick.AddListener(delegate { PreviousPlayAnimation(optionsInstance); });
            animationsHolder = assetHolderInstance;
            PlayAnimation(optionsInstance);
        }
    }

    public void NextPlayAnimation(OptionsHolder optionsHolder)
    {
        if (optionsHolder.assetList.value < optionsHolder.assetList.options.Count)
        {
            optionsHolder.assetList.value++;
            PlayAnimation(optionsHolder);
        }
    }

    public void PreviousPlayAnimation(OptionsHolder optionsHolder)
    {
        if (optionsHolder.assetList.value > 0)
        {
            optionsHolder.assetList.value--;
            PlayAnimation(optionsHolder);
        }
    }

    public void PlayAnimation(OptionsHolder optionsHolder)
    {
        currentAnimation = optionsHolder.assetList.options[optionsHolder.assetList.value].text;

        currentCharacterAnimation.Play(currentAnimation);

        foreach(var items in characters[characterCounter].itemsConfiguration)
        {
            var time = currentCharacterAnimation[currentAnimation].time;
            items.PlayAnimation(currentAnimation, time);
        }

        optionsHolder.CheckButtons();
    }

    //-----------------------------------------------------------------
    private void GeneratePresetUI()
    {
        if (presetsHolder != null)
            Destroy(presetsHolder.gameObject);

        if (presets != null)
        {
            var assetHolderInstance = Instantiate(assetHolder, rightHolder);

            presetsHolder = assetHolderInstance;

            var holderName = assetHolderInstance.GetComponentInChildren<Text>();

            if (holderName != null)
                holderName.text = "Presets";

            var optionsInstance = Instantiate(optionHodler, assetHolderInstance);

            var presetsNames = new List<string>();
            presetsNames.Add("None");

            foreach (var preset in presets.characters)
            {
                if (preset != null)
                {
                    presetsNames.Add(preset.name);
                }
            }

            optionsInstance.assetList.AddOptions(presetsNames);
            optionsInstance.assetList.value = 0;
            presetOptions = optionsInstance;
            presetOptions.CheckButtons();

            optionsInstance.assetList.onValueChanged.AddListener(delegate { ActivatePreset(); });
            optionsInstance.nextButton.onClick.AddListener(delegate { ActivateNextPreset(); });
            optionsInstance.previousButton.onClick.AddListener(delegate { ActivatePreviousPreset(); });
        }
    }

    public void ActivateNextPreset()
    {
        if (presetOptions.assetList.value < presetOptions.assetList.options.Count)
        {
            presetOptions.assetList.value++;
            ActivatePreset();
        }
    }

    public void ActivatePreviousPreset()
    {
        if (presetOptions.assetList.value > 0)
        {
            presetOptions.assetList.value--;
            ActivatePreset();
        }
    }

    public void ActivatePreset()
    {
        if (presetOptions.assetList.value != 0) //Option 0 is "None"
        {
            characters[characterCounter].gameObject.SetActive(false);

            presets.ActivateCharacter(presetOptions.assetList.value - 1, characters[characterCounter].transform.position);

            var characterRotate = GetComponent<CharacterRotate>();

            if (characterRotate != null)
                characterRotate.objectToRotate = presets.GetCurrentCharacter;

            var presetAnimations = presets.GetCurrentCharacter.GetComponent<Animation>();
            if (presetAnimations != null)
            {
                currentCharacterAnimation = presetAnimations;
                GenerateAnimationsUI();
            }
        }
        else
        {
            ActivatePlayerCharacter();
        }

        presetOptions.CheckButtons();
    }

    //-----------------------------------------------------------------
    private void ActivatePlayerCharacter()
    {
            characters[characterCounter].gameObject.SetActive(true);
            currentCharacterAnimation = characters[characterCounter].gameObject.GetComponent<Animation>();
            currentCharacterItems = characters[characterCounter];

            GenerateAnimationsUI();
            GenerateItemsUI();


        if (presets != null)
                presets.DeactivatePreset();

            var characterRotate = GetComponent<CharacterRotate>();

            if (characterRotate != null)
                characterRotate.objectToRotate = characters[characterCounter].transform;

            if (presetOptions != null)
                presetOptions.assetList.value = 0;
    }
}