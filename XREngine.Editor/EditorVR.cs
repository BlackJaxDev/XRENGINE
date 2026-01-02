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
      if (!File.Exists(path))
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
               { "ja", "ã‚°ãƒ­ãƒ¼ãƒãƒ«" },
               { "ko", "ì „ì—­" },
               { "nl", "Globaal" },
               { "pl", "Globalny" },
               { "pt", "Global" },
               { "ru", "Ð“Ð»Ð¾Ð±Ð°Ð»ÑŒÐ½Ñ‹Ð¹" },
               { "zh", "å…¨å±€" },
            },
         },
         new()
         {
            Name = EVRActionCategory.OneHanded,
            Type = ActionSetType.Single,
            LocalizedNames = new Dictionary<string, string>
            {
               { "en_us", "One Handed" },
               { "fr", "Ã€ une main" },
               { "de", "EinhÃ¤ndig" },
               { "es", "De una mano" },
               { "it", "A una mano" },
               { "ja", "ç‰‡æ‰‹" },
               { "ko", "í•œ ì†" },
               { "nl", "Eenhandig" },
               { "pl", "JednorÄ™czny" },
               { "pt", "De uma mÃ£o" },
               { "ru", "ÐžÐ´Ð½Ð¾Ð¹ Ñ€ÑƒÐºÐ¾Ð¹" },
               { "zh", "å•æ‰‹" },
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
               { "de", "SchnellmenÃ¼" },
               { "es", "MenÃº rÃ¡pido" },
               { "it", "Menu rapido" },
               { "ja", "ã‚¯ã‚¤ãƒƒã‚¯ãƒ¡ãƒ‹ãƒ¥ãƒ¼" },
               { "ko", "ë¹ ë¥¸ ë©”ë‰´" },
               { "nl", "Snelmenu" },
               { "pl", "Szybkie menu" },
               { "pt", "Menu rÃ¡pido" },
               { "ru", "Ð‘Ñ‹ÑÑ‚Ñ€Ð¾Ðµ Ð¼ÐµÐ½ÑŽ" },
               { "zh", "å¿«æ·èœå•" },
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
               { "de", "MenÃ¼" },
               { "es", "MenÃº" },
               { "it", "Menu" },
               { "ja", "ãƒ¡ãƒ‹ãƒ¥ãƒ¼" },
               { "ko", "ë©”ë‰´" },
               { "nl", "Menu" },
               { "pl", "Menu" },
               { "pt", "Menu" },
               { "ru", "ÐœÐµÐ½ÑŽ" },
               { "zh", "èœå•" },
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
               { "de", "Avatar-MenÃ¼" },
               { "es", "MenÃº de avatar" },
               { "it", "Menu avatar" },
               { "ja", "ã‚¢ãƒã‚¿ãƒ¼ãƒ¡ãƒ‹ãƒ¥ãƒ¼" },
               { "ko", "ì•„ë°”íƒ€ ë©”ë‰´" },
               { "nl", "Avatar-menu" },
               { "pl", "Menu awatara" },
               { "pt", "Menu de avatar" },
               { "ru", "ÐœÐµÐ½ÑŽ Ð°Ð²Ð°Ñ‚Ð°Ñ€Ð°" },
               { "zh", "å¤´åƒèœå•" },
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
            { "ja", "æ“ä½œ" },
            { "ko", "ìƒí˜¸ìž‘ìš©" },
            { "nl", "Interactie" },
            { "pl", "Wzajemne oddzialywanie" },
            { "pt", "Interagir" },
            { "ru", "Ð’Ð·Ð°Ð¸Ð¼Ð¾Ð´ÐµÐ¹ÑÑ‚Ð²Ð¾Ð²Ð°Ñ‚ÑŒ" },
            { "zh", "äº¤äº’" },
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
            { "ja", "ã‚¸ãƒ£ãƒ³ãƒ—" },
            { "ko", "ì í”„" },
            { "nl", "Springen" },
            { "pl", "Skok" },
            { "pt", "Pular" },
            { "ru", "ÐŸÑ€Ñ‹Ð¶Ð¾Ðº" },
            { "zh", "è·³è·ƒ" },
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
            { "fr", "Activer/DÃ©sactiver le son" },
            { "de", "Stummschaltung umschalten" },
            { "es", "Activar/Desactivar silencio" },
            { "it", "Attiva/Disattiva muto" },
            { "ja", "ãƒŸãƒ¥ãƒ¼ãƒˆåˆ‡ã‚Šæ›¿ãˆ" },
            { "ko", "ìŒì†Œê±° ì „í™˜" },
            { "nl", "Geluid dempen in-/uitschakelen" },
            { "pl", "Przelacz wyciszenie" },
            { "pt", "Alternar mudo" },
            { "ru", "ÐŸÐµÑ€ÐµÐºÐ»ÑŽÑ‡Ð¸Ñ‚ÑŒ Ð±ÐµÐ· Ð·Ð²ÑƒÐºÐ°" },
            { "zh", "åˆ‡æ¢é™éŸ³" },
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
            { "ja", "ã¤ã‹ã‚€" },
            { "ko", "ìž¡ê¸°" },
            { "nl", "Grijpen" },
            { "pl", "Chwycic" },
            { "pt", "Agarrar" },
            { "ru", "Ð¡Ñ…Ð²Ð°Ñ‚Ð¸Ñ‚ÑŒ" },
            { "zh", "æŠ“å–" },
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
            { "fr", "Glisser l'espace de jeu Ã  gauche" },
            { "de", "Playspace nach links ziehen" },
            { "es", "Arrastrar el espacio de juego a la izquierda" },
            { "it", "Trascina lo spazio di gioco a sinistra" },
            { "ja", "ãƒ—ãƒ¬ã‚¤ã‚¹ãƒšãƒ¼ã‚¹ã‚’å·¦ã¸ãƒ‰ãƒ©ãƒƒã‚°" },
            { "ko", "í”Œë ˆì´ ê³µê°„ì„ ì™¼ìª½ìœ¼ë¡œ ë“œëž˜ê·¸" },
            { "nl", "Playspace naar links slepen" },
            { "pl", "Przeciagnij obszar gry w lewo" },
            { "pt", "Arrastar o espaÃ§o de jogo para a esquerda" },
            { "ru", "ÐŸÐµÑ€ÐµÑ‚Ð°Ñ‰Ð¸Ñ‚ÑŒ Ð¸Ð³Ñ€Ð¾Ð²Ð¾Ðµ Ð¿Ñ€Ð¾ÑÑ‚Ñ€Ð°Ð½ÑÑ‚Ð²Ð¾ Ð²Ð»ÐµÐ²Ð¾" },
            { "zh", "å‘å·¦æ‹–åŠ¨æ¸¸æˆç©ºé—´" },
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
            { "fr", "Glisser l'espace de jeu Ã  droite" },
            { "de", "Playspace nach rechts ziehen" },
            { "es", "Arrastrar el espacio de juego a la derecha" },
            { "it", "Trascina lo spazio di gioco a destra" },
            { "ja", "ãƒ—ãƒ¬ã‚¤ã‚¹ãƒšãƒ¼ã‚¹ã‚’å³ã¸ãƒ‰ãƒ©ãƒƒã‚°" },
            { "ko", "í”Œë ˆì´ ê³µê°„ì„ ì˜¤ë¥¸ìª½ìœ¼ë¡œ ë“œëž˜ê·¸" },
            { "nl", "Playspace naar rechts slepen" },
            { "pl", "Przeciagnij obszar gry w prawo" },
            { "pt", "Arrastar o espaÃ§o de jogo para a direita" },
            { "ru", "ÐŸÐµÑ€ÐµÑ‚Ð°Ñ‰Ð¸Ñ‚ÑŒ Ð¸Ð³Ñ€Ð¾Ð²Ð¾Ðµ Ð¿Ñ€Ð¾ÑÑ‚Ñ€Ð°Ð½ÑÑ‚Ð²Ð¾ Ð²Ð¿Ñ€Ð°Ð²Ð¾" },
            { "zh", "å‘å³æ‹–åŠ¨æ¸¸æˆç©ºé—´" },
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
            { "de", "MenÃ¼ umschalten" },
            { "es", "Alternar menÃº" },
            { "it", "Attiva/Disattiva menu" },
            { "ja", "ãƒ¡ãƒ‹ãƒ¥ãƒ¼åˆ‡ã‚Šæ›¿ãˆ" },
            { "ko", "ë©”ë‰´ ì „í™˜" },
            { "nl", "Menu in-/uitschakelen" },
            { "pl", "Przelacz menu" },
            { "pt", "Alternar menu" },
            { "ru", "ÐŸÐµÑ€ÐµÐºÐ»ÑŽÑ‡Ð¸Ñ‚ÑŒ Ð¼ÐµÐ½ÑŽ" },
            { "zh", "åˆ‡æ¢èœå•" },
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
            { "de", "SchnellmenÃ¼ umschalten" },
            { "es", "Alternar menÃº rÃ¡pido" },
            { "it", "Attiva/Disattiva menu rapido" },
            { "ja", "ã‚¯ã‚¤ãƒƒã‚¯ãƒ¡ãƒ‹ãƒ¥ãƒ¼åˆ‡ã‚Šæ›¿ãˆ" },
            { "ko", "ë¹ ë¥¸ ë©”ë‰´ ì „í™˜" },
            { "nl", "Snelmenu in-/uitschakelen" },
            { "pl", "Przelacz szybkie menu" },
            { "pt", "Alternar menu rÃ¡pido" },
            { "ru", "ÐŸÐµÑ€ÐµÐºÐ»ÑŽÑ‡Ð¸Ñ‚ÑŒ Ð±Ñ‹ÑÑ‚Ñ€Ð¾Ðµ Ð¼ÐµÐ½ÑŽ" },
            { "zh", "åˆ‡æ¢å¿«æ·èœå•" },
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
            { "de", "Avatar-MenÃ¼ umschalten" },
            { "es", "Alternar menÃº de avatar" },
            { "it", "Attiva/Disattiva menu avatar" },
            { "ja", "ã‚¢ãƒã‚¿ãƒ¼ãƒ¡ãƒ‹ãƒ¥ãƒ¼åˆ‡ã‚Šæ›¿ãˆ" },
            { "ko", "ì•„ë°”íƒ€ ë©”ë‰´ ì „í™˜" },
            { "nl", "Avatar-menu in-/uitschakelen" },
            { "pl", "Przelacz menu awatara" },
            { "pt", "Alternar menu de avatar" },
            { "ru", "ÐŸÐµÑ€ÐµÐºÐ»ÑŽÑ‡Ð¸Ñ‚ÑŒ Ð¼ÐµÐ½ÑŽ Ð°Ð²Ð°Ñ‚Ð°Ñ€Ð°" },
            { "zh", "åˆ‡æ¢å¤´åƒèœå•" },
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
            { "ja", "å·¦æ‰‹ãƒãƒ¼ã‚º" },
            { "ko", "ì™¼ì† í¬ì¦ˆ" },
            { "nl", "Linkerhandpose" },
            { "pl", "Pozycja lewej reki" },
            { "pt", "Pose da mÃ£o esquerda" },
            { "ru", "ÐŸÐ¾Ð·Ð° Ð»ÐµÐ²Ð¾Ð¹ Ñ€ÑƒÐºÐ¸" },
            { "zh", "å·¦æ‰‹å§¿åŠ¿" },
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
            { "ja", "å³æ‰‹ãƒãƒ¼ã‚º" },
            { "ko", "ì˜¤ë¥¸ì† í¬ì¦ˆ" },
            { "nl", "Rechterhandpose" },
            { "pl", "Pozycja prawej reki" },
            { "pt", "Pose da mÃ£o direita" },
            { "ru", "ÐŸÐ¾Ð·Ð° Ð¿Ñ€Ð°Ð²Ð¾Ð¹ Ñ€ÑƒÐºÐ¸" },
            { "zh", "å³æ‰‹å§¿åŠ¿" },
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
            { "fr", "Se dÃ©placer" },
            { "de", "Fortbewegen" },
            { "es", "Desplazarse" },
            { "it", "Muoversi" },
            { "ja", "ç§»å‹•" },
            { "ko", "ì´ë™" },
            { "nl", "Verplaatsen" },
            { "pl", "Poruszanie siÄ™" },
            { "pt", "Locomover" },
            { "ru", "ÐŸÐµÑ€ÐµÐ´Ð²Ð¸Ð¶ÐµÐ½Ð¸Ðµ" },
            { "zh", "ç§»åŠ¨" },
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
            { "ja", "å›žè»¢" },
            { "ko", "íšŒì „" },
            { "nl", "Draaien" },
            { "pl", "ObrÃ³t" },
            { "pt", "Girar" },
            { "ru", "ÐŸÐ¾Ð²Ð¾Ñ€Ð¾Ñ‚" },
            { "zh", "è½¬å‘" },
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

