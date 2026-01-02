using OpenVR.NET.Manifest;
using XREngine;
using XREngine.Editor;
using ActionType = OpenVR.NET.Manifest.ActionType;

internal static class EditorVR
{
    public static void ApplyOpenVRSettings(VRGameStartupSettings<EVRActionCategory, EVRGameAction> settings)
    {
        //https://github.com/ValveSoftware/openvr/wiki/Action-manifest

        string path = Path.Combine(Directory.GetCurrentDirectory(), "bindings_knuckles.json");
        File.WriteAllText(path, KnucklesBindingsJsonContent);

        settings.ActionManifest = new ActionManifest<EVRActionCategory, EVRGameAction>()
        {
            Actions = GetActions(),
            ActionSets = GetActionSets(),
            DefaultBindings =
            [
                new DefaultBinding()
                {
                    ControllerType = "knuckles",
                    Path = "bindings_knuckles.json"
                }
            ],
        };
        settings.VRManifest = new VrManifest()
        {
            AppKey = "XRE.VR.Test",
            IsDashboardOverlay = false,
            WindowsPath = Environment.ProcessPath,
            WindowsArguments = "",
        };
    }

    #region VR Actions
    private static List<ActionSet<EVRActionCategory, EVRGameAction>> GetActionSets()
    {
        return
        [
            new()
            {
                Name = EVRActionCategory.Global,
                Type = ActionSetType.LeftRight,
                LocalizedNames = new Dictionary<string, string>
                {
                    { "en_us", "Global" },
                    { "fr", "Global" },
                    { "de", "Global" },
                    { "es", "Global" },
                    { "it", "Global" },
                    { "ja", "?????" },
                    { "ko", "???" },
                    { "nl", "Globaal" },
                    { "pl", "Globalny" },
                    { "pt", "Global" },
                    { "ru", "??????????" },
                    { "zh", "??" },
                },
            },
            new()
            {
                Name = EVRActionCategory.OneHanded,
                Type = ActionSetType.Single,
                LocalizedNames = new Dictionary<string, string>
                {
                    { "en_us", "One Handed" },
                    { "fr", "� une main" },
                    { "de", "Einh�ndig" },
                    { "es", "De una mano" },
                    { "it", "A una mano" },
                    { "ja", "??" },
                    { "ko", "? ?" },
                    { "nl", "Eenhandig" },
                    { "pl", "Jednoreki" },
                    { "pt", "De uma m�o" },
                    { "ru", "?????????" },
                    { "zh", "??" },
                },
            },
            new()
            {
                Name = EVRActionCategory.QuickMenu,
                Type = ActionSetType.Single,
                LocalizedNames = new Dictionary<string, string>
                {
                    { "en_us", "Quick Menu" },
                    { "fr", "Menu rapide" },
                    { "de", "Schnellmen�" },
                    { "es", "Men� r�pido" },
                    { "it", "Menu rapido" },
                    { "ja", "????????" },
                    { "ko", "?? ??" },
                    { "nl", "Snelmenu" },
                    { "pl", "Szybkie menu" },
                    { "pt", "Menu r�pido" },
                    { "ru", "??????? ????" },
                    { "zh", "????" },
                },
            },
            new()
            {
                Name = EVRActionCategory.Menu,
                Type = ActionSetType.Single,
                LocalizedNames = new Dictionary<string, string>
                {
                    { "en_us", "Menu" },
                    { "fr", "Menu" },
                    { "de", "Men�" },
                    { "es", "Men�" },
                    { "it", "Menu" },
                    { "ja", "????" },
                    { "ko", "??" },
                    { "nl", "Menu" },
                    { "pl", "Menu" },
                    { "pt", "Menu" },
                    { "ru", "????" },
                    { "zh", "??" },
                },
            },
            new()
            {
                Name = EVRActionCategory.AvatarMenu,
                Type = ActionSetType.Single,
                LocalizedNames = new Dictionary<string, string>
                {
                    { "en_us", "Avatar Menu" },
                    { "fr", "Menu de l'avatar" },
                    { "de", "Avatar-Men�" },
                    { "es", "Men� de avatar" },
                    { "it", "Menu avatar" },
                    { "ja", "????????" },
                    { "ko", "??? ??" },
                    { "nl", "Avatar-menu" },
                    { "pl", "Menu awatara" },
                    { "pt", "Menu de avatar" },
                    { "ru", "???? ???????" },
                    { "zh", "????" },
                },
            },
        ];
    }
    private static List<OpenVR.NET.Manifest.Action<EVRActionCategory, EVRGameAction>> GetActions() =>
    [
        new OpenVR.NET.Manifest.Action<EVRActionCategory, EVRGameAction>()
        {
            Name = EVRGameAction.Interact,
            Category = EVRActionCategory.Global,
            Type = ActionType.Boolean,
            Requirement = Requirement.Mandatory,
            LocalizedNames = new Dictionary<string, string>
            {
                { "en_us", "Interact" },
                { "fr", "Interagir" },
                { "de", "Interagieren" },
                { "es", "Interactuar" },
                { "it", "Interagire" },
                { "ja", "????" },
                { "ko", "?? ??" },
                { "nl", "Interactie" },
                { "pl", "Wzajemne oddzialywanie" },
                { "pt", "Interagir" },
                { "ru", "?????????????????" },
                { "zh", "??" },
            },
        },
        new OpenVR.NET.Manifest.Action<EVRActionCategory, EVRGameAction>()
        {
            Name = EVRGameAction.Jump,
            Category = EVRActionCategory.Global,
            Type = ActionType.Boolean,
            Requirement = Requirement.Suggested,
            LocalizedNames = new Dictionary<string, string>
            {
                { "en_us", "Jump" },
                { "fr", "Sauter" },
                { "de", "Springen" },
                { "es", "Saltar" },
                { "it", "Saltare" },
                { "ja", "????" },
                { "ko", "??" },
                { "nl", "Springen" },
                { "pl", "Skok" },
                { "pt", "Pular" },
                { "ru", "???????" },
                { "zh", "?" },
            },
        },
        new OpenVR.NET.Manifest.Action<EVRActionCategory, EVRGameAction>()
        {
            Name = EVRGameAction.ToggleMute,
            Category = EVRActionCategory.Global,
            Type = ActionType.Boolean,
            Requirement = Requirement.Optional,
            LocalizedNames = new Dictionary<string, string>
            {
                { "en_us", "Toggle Mute" },
                { "fr", "Activer/D�sactiver le son" },
                { "de", "Stummschaltung umschalten" },
                { "es", "Activar/Desactivar silencio" },
                { "it", "Attiva/Disattiva muto" },
                { "ja", "?????????" },
                { "ko", "??? ??" },
                { "nl", "Geluid dempen in-/uitschakelen" },
                { "pl", "Przelacz wyciszenie" },
                { "pt", "Alternar mudo" },
                { "ru", "??????????? ????" },
                { "zh", "????" },
            },
        },
        new OpenVR.NET.Manifest.Action<EVRActionCategory, EVRGameAction>()
        {
            Name = EVRGameAction.Grab,
            Category = EVRActionCategory.Global,
            Type = ActionType.Scalar,
            Requirement = Requirement.Mandatory,
            LocalizedNames = new Dictionary<string, string>
            {
                { "en_us", "Grab" },
                { "fr", "Saisir" },
                { "de", "Greifen" },
                { "es", "Agarrar" },
                { "it", "Afferrare" },
                { "ja", "???" },
                { "ko", "??" },
                { "nl", "Grijpen" },
                { "pl", "Chwycic" },
                { "pt", "Agarrar" },
                { "ru", "??????" },
                { "zh", "?" },
            },
        },
        new OpenVR.NET.Manifest.Action<EVRActionCategory, EVRGameAction>()
        {
            Name = EVRGameAction.PlayspaceDragLeft,
            Category = EVRActionCategory.Global,
            Type = ActionType.Boolean,
            Requirement = Requirement.Optional,
            LocalizedNames = new Dictionary<string, string>
            {
                { "en_us", "Playspace Drag Left" },
                { "fr", "Glisser l'espace de jeu � gauche" },
                { "de", "Playspace nach links ziehen" },
                { "es", "Arrastrar el espacio de juego a la izquierda" },
                { "it", "Trascina lo spazio di gioco a sinistra" },
                { "ja", "??????????????" },
                { "ko", "??? ????? ???? ???" },
                { "nl", "Playspace naar links slepen" },
                { "pl", "Przeciagnij obszar gry w lewo" },
                { "pt", "Arrastar o espa�o de jogo para a esquerda" },
                { "ru", "?????????? ??????? ???????????? ?????" },
                { "zh", "?????????" },
            },
        },
        new OpenVR.NET.Manifest.Action<EVRActionCategory, EVRGameAction>()
        {
            Name = EVRGameAction.PlayspaceDragRight,
            Category = EVRActionCategory.Global,
            Type = ActionType.Boolean,
            Requirement = Requirement.Optional,
            LocalizedNames = new Dictionary<string, string>
            {
                { "en_us", "Playspace Drag Right" },
                { "fr", "Glisser l'espace de jeu � droite" },
                { "de", "Playspace nach rechts ziehen" },
                { "es", "Arrastrar el espacio de juego a la derecha" },
                { "it", "Trascina lo spazio di gioco a destra" },
                { "ja", "??????????????" },
                { "ko", "??? ????? ????? ???" },
                { "nl", "Playspace naar rechts slepen" },
                { "pl", "Przeciagnij obszar gry w prawo" },
                { "pt", "Arrastar o espa�o de jogo para a direita" },
                { "ru", "?????????? ??????? ???????????? ??????" },
                { "zh", "?????????" },
            },
        },
        new OpenVR.NET.Manifest.Action<EVRActionCategory, EVRGameAction>()
        {
            Name = EVRGameAction.ToggleMenu,
            Category = EVRActionCategory.Global,
            Type = ActionType.Boolean,
            Requirement = Requirement.Mandatory,
            LocalizedNames = new Dictionary<string, string>
            {
                { "en_us", "Toggle Menu" },
                { "fr", "Basculer le menu" },
                { "de", "Men� umschalten" },
                { "es", "Alternar men�" },
                { "it", "Attiva/Disattiva menu" },
                { "ja", "?????????" },
                { "ko", "?? ??" },
                { "nl", "Menu in-/uitschakelen" },
                { "pl", "Przelacz menu" },
                { "pt", "Alternar menu" },
                { "ru", "??????????? ????" },
                { "zh", "????" },
            },
        },
        new OpenVR.NET.Manifest.Action<EVRActionCategory, EVRGameAction>()
        {
            Name = EVRGameAction.ToggleQuickMenu,
            Category = EVRActionCategory.Global,
            Type = ActionType.Boolean,
            Requirement = Requirement.Suggested,
            LocalizedNames = new Dictionary<string, string>
            {
                { "en_us", "Toggle Quick Menu" },
                { "fr", "Basculer le menu rapide" },
                { "de", "Schnellmen� umschalten" },
                { "es", "Alternar men� r�pido" },
                { "it", "Attiva/Disattiva menu rapido" },
                { "ja", "?????????????" },
                { "ko", "?? ?? ??" },
                { "nl", "Snelmenu in-/uitschakelen" },
                { "pl", "Przelacz szybkie menu" },
                { "pt", "Alternar menu r�pido" },
                { "ru", "??????????? ??????? ????" },
                { "zh", "??????" },
            },
        },
        new OpenVR.NET.Manifest.Action<EVRActionCategory, EVRGameAction>()
        {
            Name = EVRGameAction.ToggleAvatarMenu,
            Category = EVRActionCategory.Global,
            Type = ActionType.Boolean,
            Requirement = Requirement.Mandatory,
            LocalizedNames = new Dictionary<string, string>
            {
                { "en_us", "Toggle Avatar Menu" },
                { "fr", "Basculer le menu de l'avatar" },
                { "de", "Avatar-Men� umschalten" },
                { "es", "Alternar men� de avatar" },
                { "it", "Attiva/Disattiva menu avatar" },
                { "ja", "?????????????" },
                { "ko", "??? ?? ??" },
                { "nl", "Avatar-menu in-/uitschakelen" },
                { "pl", "Przelacz menu awatara" },
                { "pt", "Alternar menu de avatar" },
                { "ru", "??????????? ???? ???????" },
                { "zh", "??????" },
            },
        },
        new OpenVR.NET.Manifest.Action<EVRActionCategory, EVRGameAction>()
        {
            Name = EVRGameAction.LeftHandPose,
            Category = EVRActionCategory.Global,
            Type = ActionType.Pose,
            Requirement = Requirement.Mandatory,
            LocalizedNames = new Dictionary<string, string>
            {
                { "en_us", "Left Hand Pose" },
                { "fr", "Pose de la main gauche" },
                { "de", "Linke Hand Pose" },
                { "es", "Pose de la mano izquierda" },
                { "it", "Posa della mano sinistra" },
                { "ja", "??????" },
                { "ko", "?? ??" },
                { "nl", "Linkerhandpose" },
                { "pl", "Pozycja lewej reki" },
                { "pt", "Pose da m�o esquerda" },
                { "ru", "???? ????? ????" },
                { "zh", "????" },
            },
        },
        new OpenVR.NET.Manifest.Action<EVRActionCategory, EVRGameAction>()
        {
            Name = EVRGameAction.RightHandPose,
            Category = EVRActionCategory.Global,
            Type = ActionType.Pose,
            Requirement = Requirement.Mandatory,
            LocalizedNames = new Dictionary<string, string>
            {
                { "en_us", "Right Hand Pose" },
                { "fr", "Pose de la main droite" },
                { "de", "Rechte Hand Pose" },
                { "es", "Pose de la mano derecha" },
                { "it", "Posa della mano destra" },
                { "ja", "??????" },
                { "ko", "??? ??" },
                { "nl", "Rechterhandpose" },
                { "pl", "Pozycja prawej reki" },
                { "pt", "Pose da m�o direita" },
                { "ru", "???? ?????? ????" },
                { "zh", "????" },
            },
        },
        new OpenVR.NET.Manifest.Action<EVRActionCategory, EVRGameAction>()
        {
            Name = EVRGameAction.Locomote,
            Category = EVRActionCategory.Global,
            Type = ActionType.Vector2,
            Requirement = Requirement.Mandatory,
            LocalizedNames = new Dictionary<string, string>
            {
                { "en_us", "Locomote" },
                { "fr", "Se d�placer" },
                { "de", "Fortbewegen" },
                { "es", "Desplazarse" },
                { "it", "Muoversi" },
                { "ja", "??" },
                { "ko", "??" },
                { "nl", "Verplaatsen" },
                { "pl", "Poruszanie sie" },
                { "pt", "Locomover" },
                { "ru", "???????????" },
                { "zh", "??" },
            },
        },
        new OpenVR.NET.Manifest.Action<EVRActionCategory, EVRGameAction>()
        {
            Name = EVRGameAction.Turn,
            Category = EVRActionCategory.Global,
            Type = ActionType.Vector2,
            Requirement = Requirement.Mandatory,
            LocalizedNames = new Dictionary<string, string>
            {
                { "en_us", "Turn" },
                { "fr", "Tourner" },
                { "de", "Drehen" },
                { "es", "Girar" },
                { "it", "Girare" },
                { "ja", "??" },
                { "ko", "??" },
                { "nl", "Draaien" },
                { "pl", "Obr�t" },
                { "pt", "Girar" },
                { "ru", "???????" },
                { "zh", "?" },
            },
        },
    ];
    #endregion

    public static string KnucklesBindingsJsonContent = @"{
   ""action_manifest_version"" : 0,
   ""alias_info"" : {
      ""/actions/legacy/in/head_proximity"" : {
         ""alias_name"" : """",
         ""hidden"" : true
      },
      ""/actions/legacy/in/left_axis2_press"" : {
         ""alias_name"" : """",
         ""hidden"" : true
      },
      ""/actions/legacy/in/left_axis2_touch"" : {
         ""alias_name"" : """",
         ""hidden"" : true
      },
      ""/actions/legacy/in/left_axis3_press"" : {
         ""alias_name"" : """",
         ""hidden"" : true
      },
      ""/actions/legacy/in/left_axis3_touch"" : {
         ""alias_name"" : """",
         ""hidden"" : true
      },
      ""/actions/legacy/in/left_axis3_value_e0"" : {
         ""alias_name"" : """",
         ""hidden"" : true
      },
      ""/actions/legacy/in/left_axis3_value_e1"" : {
         ""alias_name"" : """",
         ""hidden"" : true
      },
      ""/actions/legacy/in/left_axis4_press"" : {
         ""alias_name"" : """",
         ""hidden"" : true
      },
      ""/actions/legacy/in/left_axis4_touch"" : {
         ""alias_name"" : """",
         ""hidden"" : true
      },
      ""/actions/legacy/in/left_axis4_value_e0"" : {
         ""alias_name"" : """",
         ""hidden"" : true
      },
      ""/actions/legacy/in/left_axis4_value_e1"" : {
         ""alias_name"" : """",
         ""hidden"" : true
      },
      ""/actions/legacy/in/left_system_press"" : {
         ""alias_name"" : """",
         ""hidden"" : true
      },
      ""/actions/legacy/in/left_system_touch"" : {
         ""alias_name"" : """",
         ""hidden"" : true
      },
      ""/actions/legacy/in/right_axis3_press"" : {
         ""alias_name"" : """",
         ""hidden"" : true
      },
      ""/actions/legacy/in/right_axis3_touch"" : {
         ""alias_name"" : """",
         ""hidden"" : true
      },
      ""/actions/legacy/in/right_axis3_value_e0"" : {
         ""alias_name"" : """",
         ""hidden"" : true
      },
      ""/actions/legacy/in/right_axis3_value_e1"" : {
         ""alias_name"" : """",
         ""hidden"" : true
      },
      ""/actions/legacy/in/right_axis4_press"" : {
         ""alias_name"" : """",
         ""hidden"" : true
      },
      ""/actions/legacy/in/right_axis4_touch"" : {
         ""alias_name"" : """",
         ""hidden"" : true
      },
      ""/actions/legacy/in/right_axis4_value_e0"" : {
         ""alias_name"" : """",
         ""hidden"" : true
      },
      ""/actions/legacy/in/right_axis4_value_e1"" : {
         ""alias_name"" : """",
         ""hidden"" : true
      },
      ""/actions/legacy/in/right_system_press"" : {
         ""alias_name"" : """",
         ""hidden"" : true
      },
      ""/actions/legacy/in/right_system_touch"" : {
         ""alias_name"" : """",
         ""hidden"" : true
      },
      ""/actions/legacy_mirrored/in/axis2_press"" : {
         ""alias_name"" : """",
         ""hidden"" : true
      },
      ""/actions/legacy_mirrored/in/axis2_touch"" : {
         ""alias_name"" : """",
         ""hidden"" : true
      },
      ""/actions/legacy_mirrored/in/axis3_press"" : {
         ""alias_name"" : """",
         ""hidden"" : true
      },
      ""/actions/legacy_mirrored/in/axis3_touch"" : {
         ""alias_name"" : """",
         ""hidden"" : true
      },
      ""/actions/legacy_mirrored/in/axis3_value_e0"" : {
         ""alias_name"" : """",
         ""hidden"" : true
      },
      ""/actions/legacy_mirrored/in/axis3_value_e1"" : {
         ""alias_name"" : """",
         ""hidden"" : true
      },
      ""/actions/legacy_mirrored/in/axis4_press"" : {
         ""alias_name"" : """",
         ""hidden"" : true
      },
      ""/actions/legacy_mirrored/in/axis4_touch"" : {
         ""alias_name"" : """",
         ""hidden"" : true
      },
      ""/actions/legacy_mirrored/in/axis4_value_e0"" : {
         ""alias_name"" : """",
         ""hidden"" : true
      },
      ""/actions/legacy_mirrored/in/axis4_value_e1"" : {
         ""alias_name"" : """",
         ""hidden"" : true
      },
      ""/actions/legacy_mirrored/in/system_press"" : {
         ""alias_name"" : """",
         ""hidden"" : true
      }
   },
   ""app_key"" : ""system.generated.xrengine.editor.exe"",
   ""bindings"" : {
      ""/actions/global"" : {
         ""poses"" : [
            {
               ""output"" : ""/actions/global/in/lefthandpose"",
               ""path"" : ""/user/hand/left/pose/base""
            },
            {
               ""output"" : ""/actions/global/in/righthandpose"",
               ""path"" : ""/user/hand/right/pose/base""
            }
         ],
         ""sources"" : [
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/global/in/interact""
                  }
               },
               ""mode"" : ""button"",
               ""path"" : ""/user/hand/left/input/trigger""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/global/in/interact""
                  }
               },
               ""mode"" : ""button"",
               ""path"" : ""/user/hand/right/input/trigger""
            },
            {
               ""inputs"" : {
                  ""held"" : {
                     ""output"" : ""/actions/global/in/playspacedragleft""
                  }
               },
               ""mode"" : ""button"",
               ""path"" : ""/user/hand/left/input/trackpad""
            },
            {
               ""inputs"" : {
                  ""held"" : {
                     ""output"" : ""/actions/global/in/playspacedragright""
                  }
               },
               ""mode"" : ""button"",
               ""path"" : ""/user/hand/right/input/trackpad""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/global/in/toggleavatarmenu""
                  }
               },
               ""mode"" : ""toggle_button"",
               ""path"" : ""/user/hand/left/input/a""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/global/in/jump""
                  }
               },
               ""mode"" : ""button"",
               ""path"" : ""/user/hand/right/input/b""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/global/in/togglemute""
                  }
               },
               ""mode"" : ""toggle_button"",
               ""path"" : ""/user/hand/right/input/a""
            },
            {
               ""inputs"" : {
                  ""grab"" : {
                     ""output"" : ""/actions/global/in/grab""
                  }
               },
               ""mode"" : ""grab"",
               ""path"" : ""/user/hand/right/input/grip""
            },
            {
               ""inputs"" : {
                  ""grab"" : {
                     ""output"" : ""/actions/global/in/grab""
                  }
               },
               ""mode"" : ""grab"",
               ""path"" : ""/user/hand/left/input/grip""
            },
            {
               ""inputs"" : {
                  ""position"" : {
                     ""output"" : ""/actions/global/in/locomote""
                  }
               },
               ""mode"" : ""joystick"",
               ""path"" : ""/user/hand/left/input/thumbstick""
            },
            {
               ""inputs"" : {
                  ""position"" : {
                     ""output"" : ""/actions/global/in/turn""
                  }
               },
               ""mode"" : ""joystick"",
               ""path"" : ""/user/hand/right/input/thumbstick""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/global/in/togglequickmenu""
                  },
                  ""long"" : {
                     ""output"" : ""/actions/global/in/togglemenu""
                  }
               },
               ""mode"" : ""button"",
               ""path"" : ""/user/hand/left/input/b""
            }
         ]
      },
      ""/actions/legacy"" : {
         ""haptics"" : [
            {
               ""output"" : ""/actions/legacy/out/left_haptic"",
               ""path"" : ""/user/hand/left/output/haptic""
            },
            {
               ""output"" : ""/actions/legacy/out/right_haptic"",
               ""path"" : ""/user/hand/right/output/haptic""
            }
         ],
         ""poses"" : [
            {
               ""output"" : ""/actions/legacy/in/Left_Pose"",
               ""path"" : ""/user/hand/left/pose/raw""
            },
            {
               ""output"" : ""/actions/legacy/in/Right_Pose"",
               ""path"" : ""/user/hand/right/pose/raw""
            }
         ],
         ""sources"" : [
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/legacy/in/left_system_press""
                  }
               },
               ""mode"" : ""button"",
               ""path"" : ""/user/hand/left/input/system""
            },
            {
               ""inputs"" : {
                  ""pull"" : {
                     ""output"" : ""/actions/legacy/in/left_axis1_value""
                  },
                  ""touch"" : {
                     ""output"" : ""/actions/legacy/in/left_axis1_touch""
                  }
               },
               ""mode"" : ""trigger"",
               ""path"" : ""/user/hand/left/input/trigger""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/legacy/in/right_system_press""
                  }
               },
               ""mode"" : ""button"",
               ""path"" : ""/user/hand/right/input/system""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/legacy/in/right_grip_press""
                  },
                  ""touch"" : {
                     ""output"" : ""/actions/legacy/in/right_grip_touch""
                  }
               },
               ""mode"" : ""button"",
               ""path"" : ""/user/hand/right/input/a""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/legacy/in/left_grip_press""
                  },
                  ""touch"" : {
                     ""output"" : ""/actions/legacy/in/left_grip_touch""
                  }
               },
               ""mode"" : ""button"",
               ""path"" : ""/user/hand/left/input/a""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/legacy/in/left_applicationmenu_press""
                  },
                  ""touch"" : {
                     ""output"" : ""/actions/legacy/in/left_applicationmenu_touch""
                  }
               },
               ""mode"" : ""button"",
               ""path"" : ""/user/hand/left/input/b""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/legacy/in/right_applicationmenu_press""
                  },
                  ""touch"" : {
                     ""output"" : ""/actions/legacy/in/right_applicationmenu_touch""
                  }
               },
               ""mode"" : ""button"",
               ""path"" : ""/user/hand/right/input/b""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/legacy/in/left_axis0_press""
                  },
                  ""position"" : {
                     ""output"" : ""/actions/legacy/in/left_axis0_value""
                  },
                  ""touch"" : {
                     ""output"" : ""/actions/legacy/in/left_axis0_touch""
                  }
               },
               ""mode"" : ""trackpad"",
               ""path"" : ""/user/hand/left/input/trackpad""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/legacy/in/right_axis0_press""
                  },
                  ""position"" : {
                     ""output"" : ""/actions/legacy/in/right_axis0_value""
                  },
                  ""touch"" : {
                     ""output"" : ""/actions/legacy/in/right_axis0_touch""
                  }
               },
               ""mode"" : ""trackpad"",
               ""path"" : ""/user/hand/right/input/trackpad""
            },
            {
               ""inputs"" : {
                  ""pull"" : {
                     ""output"" : ""/actions/legacy/in/right_axis1_value""
                  },
                  ""touch"" : {
                     ""output"" : ""/actions/legacy/in/right_axis1_touch""
                  }
               },
               ""mode"" : ""trigger"",
               ""path"" : ""/user/hand/right/input/trigger""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/legacy/in/left_axis1_press""
                  },
                  ""touch"" : {
                     ""output"" : ""/actions/legacy/in/left_axis1_touch""
                  }
               },
               ""mode"" : ""button"",
               ""parameters"" : {
                  ""click_activate_threshold"" : ""0.55"",
                  ""click_deactivate_threshold"" : ""0.5"",
                  ""haptic_amplitude"" : ""0"",
                  ""touch_activate_threshold"" : ""0.1"",
                  ""touch_deactivate_threshold"" : ""0.05""
               },
               ""path"" : ""/user/hand/left/input/trigger""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/legacy/in/right_axis1_press""
                  },
                  ""touch"" : {
                     ""output"" : ""/actions/legacy/in/right_axis1_touch""
                  }
               },
               ""mode"" : ""button"",
               ""parameters"" : {
                  ""click_activate_threshold"" : ""0.55"",
                  ""click_deactivate_threshold"" : ""0.5"",
                  ""haptic_amplitude"" : ""0"",
                  ""touch_activate_threshold"" : ""0.1"",
                  ""touch_deactivate_threshold"" : ""0.05""
               },
               ""path"" : ""/user/hand/right/input/trigger""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/legacy/in/left_axis0_press""
                  }
               },
               ""mode"" : ""button"",
               ""path"" : ""/user/hand/left/input/trackpad""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/legacy/in/right_axis0_press""
                  }
               },
               ""mode"" : ""button"",
               ""path"" : ""/user/hand/right/input/trackpad""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/legacy/in/left_axis0_press""
                  },
                  ""position"" : {
                     ""output"" : ""/actions/legacy/in/left_axis0_value""
                  },
                  ""touch"" : {
                     ""output"" : ""/actions/legacy/in/left_axis0_touch""
                  }
               },
               ""mode"" : ""joystick"",
               ""path"" : ""/user/hand/left/input/thumbstick""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/legacy/in/right_axis0_press""
                  },
                  ""position"" : {
                     ""output"" : ""/actions/legacy/in/right_axis0_value""
                  },
                  ""touch"" : {
                     ""output"" : ""/actions/legacy/in/right_axis0_touch""
                  }
               },
               ""mode"" : ""joystick"",
               ""path"" : ""/user/hand/right/input/thumbstick""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/legacy/in/left_axis0_press""
                  }
               },
               ""mode"" : ""button"",
               ""parameters"" : {
                  ""click_activate_threshold"" : 0.80000000000000004,
                  ""click_deactivate_threshold"" : 0.69999999999999996,
                  ""force_input"" : ""position"",
                  ""haptic_amplitude"" : 0
               },
               ""path"" : ""/user/hand/left/input/thumbstick""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/legacy/in/right_axis0_press""
                  }
               },
               ""mode"" : ""button"",
               ""parameters"" : {
                  ""click_activate_threshold"" : 0.80000000000000004,
                  ""click_deactivate_threshold"" : 0.69999999999999996,
                  ""force_input"" : ""position"",
                  ""haptic_amplitude"" : 0
               },
               ""path"" : ""/user/hand/right/input/thumbstick""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/legacy/in/left_grip_press""
                  },
                  ""touch"" : {
                     ""output"" : ""/actions/legacy/in/left_grip_touch""
                  }
               },
               ""mode"" : ""button"",
               ""parameters"" : {
                  ""click_activate_threshold"" : ""0.8"",
                  ""force_input"" : ""force""
               },
               ""path"" : ""/user/hand/left/input/grip""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/legacy/in/right_grip_press""
                  },
                  ""touch"" : {
                     ""output"" : ""/actions/legacy/in/right_grip_touch""
                  }
               },
               ""mode"" : ""button"",
               ""parameters"" : {
                  ""click_activate_threshold"" : ""0.8"",
                  ""force_input"" : ""force""
               },
               ""path"" : ""/user/hand/right/input/grip""
            }
         ]
      },
      ""/actions/legacy_mirrored"" : {
         ""haptics"" : [
            {
               ""output"" : ""/actions/legacy_mirrored/out/haptic"",
               ""path"" : ""/user/hand/left/output/haptic""
            },
            {
               ""output"" : ""/actions/legacy_mirrored/out/haptic"",
               ""path"" : ""/user/hand/right/output/haptic""
            }
         ],
         ""poses"" : [
            {
               ""output"" : ""/actions/legacy_mirrored/in/pose"",
               ""path"" : ""/user/hand/left/pose/raw""
            },
            {
               ""output"" : ""/actions/legacy_mirrored/in/pose"",
               ""path"" : ""/user/hand/right/pose/raw""
            }
         ],
         ""sources"" : [
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/legacy_mirrored/in/axis1_press""
                  },
                  ""pull"" : {
                     ""output"" : ""/actions/legacy_mirrored/in/axis1_value""
                  },
                  ""touch"" : {
                     ""output"" : ""/actions/legacy_mirrored/in/axis1_touch""
                  }
               },
               ""mode"" : ""trigger"",
               ""path"" : ""/user/hand/left/input/trigger""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/legacy_mirrored/in/axis1_press""
                  },
                  ""pull"" : {
                     ""output"" : ""/actions/legacy_mirrored/in/axis1_value""
                  },
                  ""touch"" : {
                     ""output"" : ""/actions/legacy_mirrored/in/axis1_touch""
                  }
               },
               ""mode"" : ""trigger"",
               ""path"" : ""/user/hand/right/input/trigger""
            },
            {
               ""inputs"" : {
                  ""position"" : {
                     ""output"" : ""/actions/legacy_mirrored/in/axis0_value""
                  },
                  ""touch"" : {
                     ""output"" : ""/actions/legacy_mirrored/in/axis0_touch""
                  }
               },
               ""mode"" : ""trackpad"",
               ""path"" : ""/user/hand/left/input/trackpad""
            },
            {
               ""inputs"" : {
                  ""position"" : {
                     ""output"" : ""/actions/legacy_mirrored/in/axis0_value""
                  },
                  ""touch"" : {
                     ""output"" : ""/actions/legacy_mirrored/in/axis0_touch""
                  }
               },
               ""mode"" : ""trackpad"",
               ""path"" : ""/user/hand/right/input/trackpad""
            },
            {
               ""inputs"" : {},
               ""mode"" : ""joystick"",
               ""path"" : ""/user/hand/left/input/thumbstick""
            },
            {
               ""inputs"" : {},
               ""mode"" : ""joystick"",
               ""path"" : ""/user/hand/right/input/thumbstick""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/legacy_mirrored/in/grip_press""
                  },
                  ""touch"" : {
                     ""output"" : ""/actions/legacy_mirrored/in/grip_touch""
                  }
               },
               ""mode"" : ""button"",
               ""path"" : ""/user/hand/left/input/a""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/legacy_mirrored/in/grip_press""
                  },
                  ""touch"" : {
                     ""output"" : ""/actions/legacy_mirrored/in/grip_touch""
                  }
               },
               ""mode"" : ""button"",
               ""path"" : ""/user/hand/right/input/a""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/legacy_mirrored/in/applicationmenu_press""
                  },
                  ""touch"" : {
                     ""output"" : ""/actions/legacy_mirrored/in/applicationmenu_touch""
                  }
               },
               ""mode"" : ""button"",
               ""path"" : ""/user/hand/left/input/b""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/legacy_mirrored/in/applicationmenu_press""
                  },
                  ""touch"" : {
                     ""output"" : ""/actions/legacy_mirrored/in/applicationmenu_touch""
                  }
               },
               ""mode"" : ""button"",
               ""path"" : ""/user/hand/right/input/b""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/legacy_mirrored/in/axis0_press""
                  }
               },
               ""mode"" : ""button"",
               ""path"" : ""/user/hand/left/input/trackpad""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/legacy_mirrored/in/axis0_press""
                  }
               },
               ""mode"" : ""button"",
               ""path"" : ""/user/hand/right/input/trackpad""
            }
         ]
      }
   },
   ""category"" : ""steamvr_input"",
   ""controller_type"" : ""knuckles"",
   ""description"" : ""Default binding values for legacy apps using the Index Controller"",
   ""interaction_profile"" : """",
   ""name"" : ""Saved XREngine.Editor configuration for Index Controller"",
   ""options"" : {
      ""mirror_actions"" : false
   },
   ""simulated_actions"" : []
}";
}
