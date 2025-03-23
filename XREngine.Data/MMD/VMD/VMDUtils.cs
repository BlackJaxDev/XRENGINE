using System.Text;

namespace XREngine.Data.MMD
{
    public static class VMDUtils
    {
        public const float MMDUnitsToMeters = 0.08f;
        public const float MetersToMMDUnits = 12.5f;

        static VMDUtils()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        // Bone names
        public const string Mother = "全ての親";
        public const string Groove = "グルーブ";
        public const string Center = "センター";
        public const string UpperBody = "上半身";
        public const string Neck = "首";
        public const string Head = "頭";
        public const string LeftEye = "左目";
        public const string LowerBody = "下半身";
        public const string LeftShoulder = "左肩";
        public const string LeftArm = "左腕";
        public const string LeftElbow = "左ひじ";
        public const string LeftWrist = "左手首";
        public const string LeftThumb1 = "左親指１";
        public const string LeftThumb2 = "左親指２";
        public const string LeftFore1 = "左人指１";
        public const string LeftFore2 = "左人指２";
        public const string LeftFore3 = "左人指３";
        public const string LeftMiddle1 = "左中指１";
        public const string LeftMiddle2 = "左中指２";
        public const string LeftMiddle3 = "左中指３";
        public const string LeftThird1 = "左薬指１";
        public const string LeftThird2 = "左薬指２";
        public const string LeftThird3 = "左薬指３";
        public const string LeftLittle1 = "左小指１";
        public const string LeftLittle2 = "左小指２";
        public const string LeftLittle3 = "左小指３";
        public const string LeftLeg = "左足";
        public const string LeftKnee = "左ひざ";
        public const string LeftAnkle = "左足首";
        public const string BothEyes = "両目";
        public const string RightEye = "右目";
        public const string RightShoulder = "右肩";
        public const string RightArm = "右腕";
        public const string RightElbow = "右ひじ";
        public const string RightWrist = "右手首";
        public const string RightThumb1 = "右親指１";
        public const string RightThumb2 = "右親指２";
        public const string RightFore1 = "右人指１";
        public const string RightFore2 = "右人指２";
        public const string RightFore3 = "右人指３";
        public const string RightMiddle1 = "右中指１";
        public const string RightMiddle2 = "右中指２";
        public const string RightMiddle3 = "右中指３";
        public const string RightThird1 = "右薬指１";
        public const string RightThird2 = "右薬指２";
        public const string RightThird3 = "右薬指３";
        public const string RightLittle1 = "右小指１";
        public const string RightLittle2 = "右小指２";
        public const string RightLittle3 = "右小指３";
        public const string RightLeg = "右足";
        public const string RightKnee = "右ひざ";
        public const string RightAnkle = "右足首";

        public const string Waist = "腰";
        public const string UpperBody2 = "上半身2";

        // IK Bones
        public const string ArmIKLeft = "左腕ＩＫ";
        public const string ArmIKRight = "右腕ＩＫ";
        public const string ToeIKLeft = "左つま先ＩＫ";
        public const string ToeIKRight = "右つま先ＩＫ";
        public const string LeftFootIK = "左足ＩＫ";
        public const string RightFootIK = "右足ＩＫ";

        // Groups - Bone Group Mapping as per specification
        public static readonly List<(string JapaneseName, string EnglishName, string Notes)> BoneGroups =
        [
            ("Root", "Root", "Only “全ての親” is included"),
            ("表情", "Exp", "All Facial Morphs"),
            ("ＩＫ", "IK", "All IK Bones"),
            ("センター", "Center", "Waist is also included"),
            ("体(上)", "Upper Body", "Head and Neck are also included"),
            ("腕", "Arms", ""),
            ("指", "Fingers", ""),
            ("体(下)", "Lower Body", "Only Lower Body is included"),
            ("足", "Legs", "Leg IK are in IK"),
            ("その他", "Others", "Eyes, teeth, and tongue are included"),
            ("髪", "Hair", "All Hair Bones"),
            ("ｽｶｰﾄ", "Skirt", "All Skirt Bones"),
        ];

        // Unknown bone names group 1
        public const string ControlCenter = "操作中心"; // 操作中心
        public const string RightEyeReset = "右目戻"; // 右目戻
        public const string LeftEyeReset = "左目戻"; // 左目戻
        public const string Glasses = "メガネ"; // メガネ
        public const string Tongue1 = "舌１"; // 舌１
        public const string Tongue2 = "舌２"; // 舌２
        public const string Tongue3 = "舌３"; // 舌３
        public const string RightShoulderPivot = "右肩P"; // 右肩P
        public const string RightShoulderControl = "右肩C"; // 右肩C
        public const string RightArmTwist = "右腕捩"; // 右腕捩
        public const string RightArmTwist1 = "右腕捩1"; // 右腕捩1
        public const string RightArmTwist2 = "右腕捩2"; // 右腕捩2
        public const string RightArmTwist3 = "右腕捩3"; // 右腕捩3
        public const string RightHandTwist = "右手捩"; // 右手捩
        public const string RightDummy = "右ダミー"; // 右ダミー
        public const string RightThumb0 = "右親指０"; // 右親指０
        public const string RightHandTwist1 = "右手捩1"; // 右手捩1
        public const string RightHandTwist2 = "右手捩2"; // 右手捩2
        public const string RightHandTwist3 = "右手捩3"; // 右手捩3
        public const string RightSleeve = "右袖"; // 右袖
        public const string LeftShoulderPivot = "左肩P"; // 左肩P
        public const string LeftShoulderControl = "左肩C"; // 左肩C
        public const string LeftArmTwist = "左腕捩"; // 左腕捩
        public const string LeftArmTwist1 = "左腕捩1"; // 左腕捩1
        public const string LeftArmTwist2 = "左腕捩2"; // 左腕捩2
        public const string LeftArmTwist3 = "左腕捩3"; // 左腕捩3
        public const string LeftHandTwist = "左手捩"; // 左手捩
        public const string LeftDummy = "左ダミー"; // 左ダミー
        public const string LeftThumb0 = "左親指０"; // 左親指０
        public const string LeftHandTwist1 = "左手捩1"; // 左手捩1
        public const string LeftHandTwist2 = "左手捩2"; // 左手捩2
        public const string LeftHandTwist3 = "左手捩3"; // 左手捩3
        public const string LeftSleeve = "左袖"; // 左袖
        public const string NeckTie0 = "ﾈｸﾀｲ０"; // ﾈｸﾀｲ０
        public const string NeckTie1 = "ﾈｸﾀｲ１"; // ﾈｸﾀｲ１
        public const string NeckTie2 = "ﾈｸﾀｲ２"; // ﾈｸﾀｲ２
        public const string NeckTie3 = "ﾈｸﾀｲ３"; // ﾈｸﾀｲ３
        public const string NeckTie4 = "ﾈｸﾀｲ４"; // ﾈｸﾀｲ４
        public const string NeckTie4_ = "ﾈｸﾀｲ４_"; // ﾈｸﾀｲ４_
        public const string NeckTieIK = "ﾈｸﾀｲＩＫ"; // ﾈｸﾀｲＩＫ
        public const string WaistCancelRight = "腰キャンセル右"; // 腰キャンセル右
        public const string RightLegIKParent = "右足IK親"; // 右足IK親
        public const string Ankle_R_ = "足首_R_"; // 足首_R_
        public const string WaistCancelLeft = "腰キャンセル左"; // 腰キャンセル左
        public const string LeftLegIKParent = "左足IK親"; // 左足IK親
        public const string Ankle_L_ = "足首_L_"; // 足首_L_
        public const string RightLegD = "右足D"; // 右足D
        public const string RightKneeD = "右ひざD"; // 右ひざD
        public const string RightAnkleD = "右足首D"; // 右足首D
        public const string RightToeEX = "右足先EX"; // 右足先EX
        public const string LeftLegD = "左足D"; // 左足D
        public const string LeftKneeD = "左ひざD"; // 左ひざD
        public const string LeftAnkleD = "左足首D"; // 左足首D
        public const string LeftToeEX = "左足先EX"; // 左足先EX
        public const string NewBone1 = "新規ボーン1"; // 新規ボーン1
        public const string Skirt_0_0 = "スカート_0_0"; // スカート_0_0
        public const string Skirt_0_1 = "スカート_0_1"; // スカート_0_1
        public const string Skirt_0_2 = "スカート_0_2"; // スカート_0_2
        public const string Skirt_0_3 = "スカート_0_3"; // スカート_0_3
        public const string Skirt_0_4 = "スカート_0_4"; // スカート_0_4
        public const string Skirt_0_5 = "スカート_0_5"; // スカート_0_5
        public const string Skirt_0_6 = "スカート_0_6"; // スカート_0_6
        public const string Skirt_0_7 = "スカート_0_7"; // スカート_0_7
        public const string Skirt_1_0 = "スカート_1_0"; // スカート_1_0
        public const string Skirt_1_1 = "スカート_1_1"; // スカート_1_1
        public const string Skirt_1_2 = "スカート_1_2"; // スカート_1_2
        public const string Skirt_1_3 = "スカート_1_3"; // スカート_1_3
        public const string Skirt_1_4 = "スカート_1_4"; // スカート_1_4
        public const string Skirt_1_5 = "スカート_1_5"; // スカート_1_5
        public const string Skirt_1_6 = "スカート_1_6"; // スカート_1_6
        public const string Skirt_1_7 = "スカート_1_7"; // スカート_1_7
        public const string Skirt_2_0 = "スカート_2_0"; // スカート_2_0
        public const string Skirt_2_1 = "スカート_2_1"; // スカート_2_1
        public const string Skirt_2_2 = "スカート_2_2"; // スカート_2_2
        public const string Skirt_2_3 = "スカート_2_3"; // スカート_2_3
        public const string Skirt_2_4 = "スカート_2_4"; // スカート_2_4
        public const string Skirt_2_5 = "スカート_2_5"; // スカート_2_5
        public const string Skirt_2_6 = "スカート_2_6"; // スカート_2_6
        public const string Skirt_2_7 = "スカート_2_7"; // スカート_2_7
        public const string Skirt_3_0 = "スカート_3_0"; // スカート_3_0
        public const string Skirt_3_1 = "スカート_3_1"; // スカート_3_1
        public const string Skirt_3_2 = "スカート_3_2"; // スカート_3_2
        public const string Skirt_3_3 = "スカート_3_3"; // スカート_3_3
        public const string Skirt_3_4 = "スカート_3_4"; // スカート_3_4
        public const string Skirt_3_5 = "スカート_3_5"; // スカート_3_5
        public const string Skirt_3_6 = "スカート_3_6"; // スカート_3_6
        public const string Skirt_3_7 = "スカート_3_7"; // スカート_3_7
        public const string Belt1 = "ベルト1"; // ベルト1
        public const string Belt = "ベルト"; // ベルト
        public const string RightElbowHelper = "右ひじ補助"; // 右ひじ補助
        public const string RightElbowHelper1 = "右ひじ補助1"; // 右ひじ補助1
        public const string LeftElbowHelper = "左ひじ補助"; // 左ひじ補助
        public const string LeftElbowHelper1 = "左ひじ補助1"; // 左ひじ補助1
        public const string AdditionalRightElbowHelper = "+右ひじ補助"; // +右ひじ補助
        public const string RightHandEnd = "右手先"; // 右手先
        public const string RightThumbEnd = "右親指先"; // 右親指先
        public const string RightLittleFingerEnd = "右小指先"; // 右小指先
        public const string RightRingFingerEnd = "右薬指先"; // 右薬指先
        public const string RightMiddleFingerEnd = "右中指先"; // 右中指先
        public const string RightIndexFingerEnd = "右人指先"; // 右人指先
        public const string AdditionalLeftElbowHelper = "+左ひじ補助"; // +左ひじ補助
        public const string LeftHandEnd = "左手先"; // 左手先
        public const string LeftThumbEnd = "左親指先"; // 左親指先
        public const string LeftLittleFingerEnd = "左小指先"; // 左小指先
        public const string LeftRingFingerEnd = "左薬指先"; // 左薬指先
        public const string LeftMiddleFingerEnd = "左中指先"; // 左中指先
        public const string LeftIndexFingerEnd = "左人指先"; // 左人指先
        public const string StrayHair1 = "アホ毛１"; // アホ毛１
        public const string StrayHair2 = "アホ毛２"; // アホ毛２
        public const string RightSideburn1 = "右もみあげ１"; // 右もみあげ１
        public const string RightSideburn2 = "右もみあげ２"; // 右もみあげ２
        public const string LeftSideburn1 = "左もみあげ１"; // 左もみあげ１
        public const string LeftSideburn2 = "左もみあげ２"; // 左もみあげ２
        public const string FrontBangs1 = "前髪１"; // 前髪１
        public const string FrontBangs1_2 = "前髪１_２"; // 前髪１_２
        public const string FrontBangs2 = "前髪２"; // 前髪２
        public const string FrontBangs2_2 = "前髪２_２"; // 前髪２_２
        public const string FrontBangs3 = "前髪３"; // 前髪３
        public const string FrontBangs3_2 = "前髪３_２"; // 前髪３_２
        public const string RightSideburn1Shi = "右もみあげ１シ・"; // 右もみあげ１シ・
        public const string RightSideburn2Shi = "右もみあげ２シ・"; // 右もみあげ２シ・
        public const string LeftSideburn1Shi = "左もみあげ１シ・"; // 左もみあげ１シ・
        public const string LeftSideburn2Shi = "左もみあげ２シ・"; // 左もみあげ２シ・
        public const string BackHairMiddleShort = "後ろ髪中ショ"; // 後ろ髪中ショ
        public const string BackHairRightShort = "後ろ髪右ショ"; // 後ろ髪右ショ
        public const string BackHairLeftShort = "後ろ髪左ショ"; // 後ろ髪左ショ
        public const string RightHair1 = "右髪１"; // 右髪１
        public const string RightHair2 = "右髪２"; // 右髪２
        public const string RightHair3 = "右髪３"; // 右髪３
        public const string RightHair4 = "右髪４"; // 右髪４
        public const string RightHair5 = "右髪５"; // 右髪５
        public const string RightHair6 = "右髪６"; // 右髪６
        public const string RightHair7 = "右髪７"; // 右髪７
        public const string RightHair8 = "右髪８"; // 右髪８
        public const string Hair8_R_ = "髪８_R_"; // 髪８_R_
        public const string RightHairIK = "右髪ＩＫ"; // 右髪ＩＫ
        public const string LeftHair1 = "左髪１"; // 左髪１
        public const string LeftHair2 = "左髪２"; // 左髪２
        public const string LeftHair3 = "左髪３"; // 左髪３
        public const string LeftHair4 = "左髪４"; // 左髪４
        public const string LeftHair5 = "左髪５"; // 左髪５
        public const string LeftHair6 = "左髪６"; // 左髪６
        public const string LeftHair7 = "左髪７"; // 左髪７
        public const string LeftHair8 = "左髪８"; // 左髪８
        public const string Hair8_L_ = "髪８_L_"; // 髪８_L_
        public const string LeftHairIK = "左髪ＩＫ"; // 左髪ＩＫ
        public const string IndicatorC1 = "インジケータC1"; // インジケータC1
        public const string IndicatorC2 = "インジケータC2"; // インジケータC2
        public const string IndicatorCG = "インジケータCG"; // インジケータCG

        // Unknown bone names group 2
        public const string HideModel = "モデル消す"; // モデル消す
        public const string Blink = "まばたき"; // まばたき
        public const string Laugh = "笑い"; // 笑い
        public const string Wink = "ウィンク"; // ウィンク
        public const string WinkRight = "ウィンク右"; // ウィンク右
        public const string Wink2 = "ウィンク２"; // ウィンク２
        public const string Wink2Right = "ｳｨﾝｸ２右"; // ｳｨﾝｸ２右
        public const string Calm = "なごみ"; // なごみ
        public const string Hau = "はぅ"; // はぅ
        public const string Surprised = "びっくり"; // びっくり
        public const string Glare = "じと目"; // じと目
        public const string Kiri = "ｷﾘｯ"; // ｷﾘｯ
        public const string HachuEyes = "はちゅ目"; // はちゅ目
        public const string HachuEyesVerticalSquash = "はちゅ目縦潰れ"; // はちゅ目縦潰れ
        public const string HachuEyesHorizontalSquash = "はちゅ目横潰れ"; // はちゅ目横潰れ
        public const string A = "あ"; // あ
        public const string I = "い"; // い
        public const string U = "う"; // う
        public const string E = "え"; // え
        public const string O = "お"; // お
        public const string A2 = "あ２"; // あ２
        public const string N = "ん"; // ん
        public const string Triangle = "▲"; // ▲
        public const string UpCaret = "∧"; // ∧
        public const string Square = "□"; // □
        public const string Wa = "ワ"; // ワ
        public const string Omega = "ω"; // ω
        public const string OmegaSquare = "ω□"; // ω□
        public const string Smirk = "にやり"; // にやり
        public const string Smirk2 = "にやり２"; // にやり２
        public const string Smiley = "にっこり"; // にっこり
        public const string TongueOut = "ぺろっ"; // ぺろっ
        public const string Tehepero = "てへぺろ"; // てへぺろ
        public const string Tehepero2 = "てへぺろ２"; // てへぺろ２
        public const string MouthCornerUp = "口角上げ"; // 口角上げ
        public const string MouthCornerDown = "口角下げ"; // 口角下げ
        public const string MouthWide = "口横広げ"; // 口横広げ
        public const string NoTeethUp = "歯無し上"; // 歯無し上
        public const string NoTeethDown = "歯無し下"; // 歯無し下
        public const string Tears = "涙"; // 涙
        public const string Outline = "輪郭"; // 輪郭
        public const string StarEyes = "星目"; // 星目
        public const string Heart = "はぁと"; // はぁと
        public const string SmallPupils = "瞳小"; // 瞳小
        public const string PupilsVerticalSquash = "瞳縦潰れ"; // 瞳縦潰れ
        public const string UnderLight = "光下"; // 光下
        public const string TerrifyingChild = "恐ろしい子！"; // 恐ろしい子！
        public const string Embarrassed = "照れ"; // 照れ
        public const string Gasp = "がーん"; // がーん
        public const string Serious = "真面目"; // 真面目
        public const string Troubled = "困る"; // 困る
        public const string Smiling = "にこり"; // にこり
        public const string Anger = "怒り"; // 怒り
        public const string Up = "上"; // 上
        public const string Down = "下"; // 下
        public const string Front = "前"; // 前
        public const string LeftEyebrow = "眉頭左"; // 眉頭左
        public const string RightEyebrow = "眉頭右"; // 眉頭右
        public const string GlassesDuplicate = "メガネ"; // メガネ (duplicate)
        public const string TwinTeOn = "ツインテon"; // ツインテon
        public const string HipRelativeOn = "腰相対on"; // 腰相対on
        public const string GWarningOff = "G警告Off"; // G警告Off
        public const string CWarningOff = "C警告Off"; // C警告Off
        public const string FootGroundingOn = "足接地On"; // 足接地On
        public const string IndicatorOff = "インジケ－タ消"; // インジケ－タ消

        public static string ToShiftJisString(byte[] byteString)
            => Encoding.GetEncoding("Shift-JIS").GetString(byteString).Split('\0')[0];
        public static byte[] ToShiftJisBytes(string str)
            => Encoding.GetEncoding("Shift-JIS").GetBytes(str);

        public static readonly Dictionary<string, string> JP2EN = new()
        {
            { Mother, "Mother" },
            { Groove, "RootNode" },
            { Center, "Armature" },
            { Waist, "Waist" },
            { LowerBody, "Hips" },
            { UpperBody, "Spine" },
            { UpperBody2, "Chest" },
            { Neck, "Neck" },
            { Head, "Head" },

            { BothEyes, "Eyes" },
            { LeftEye, "Eye_L" },
            { RightEye, "Eye_R" },

            { LeftShoulder, "Left_shoulder" },
            { LeftShoulderPivot, "Left_shoulder_p" }, // 左肩P
            { LeftShoulderControl, "Left_shoulder_c" }, // 左肩C
            { LeftArm, "Left_arm" },
            { LeftElbow, "Left_elbow" },
            { LeftWrist, "Left_wrist" },
            { LeftLeg, "Left_leg" },
            { LeftKnee, "Left_knee" },
            { LeftAnkle, "Left_ankle" },
            { RightShoulder, "Right_shoulder" },
            { RightShoulderPivot, "Right_shoulder_p" },    // 右肩P
            { RightShoulderControl, "Right_shoulder_c" },  // 右肩C
            { RightArm, "Right_arm" },
            { RightElbow, "Right_elbow" },
            { RightWrist, "Right_wrist" },

            { RightLeg, "Right_leg" },
            { RightKnee, "Right_knee" },
            { RightAnkle, "Right_ankle" },

            { LeftThumb1, "Thumb0_L" },
            { LeftThumb2, "Thumb1_L" },
            { LeftFore1, "IndexFinger1_L" },
            { LeftFore2, "IndexFinger2_L" },
            { LeftFore3, "IndexFinger3_L" },
            { LeftMiddle1, "MiddleFinger1_L" },
            { LeftMiddle2, "MiddleFinger2_L" },
            { LeftMiddle3, "MiddleFinger3_L" },
            { LeftThird1, "RingFinger1_L" },
            { LeftThird2, "RingFinger2_L" },
            { LeftThird3, "RingFinger3_L" },
            { LeftLittle1, "LittleFinger1_L" },
            { LeftLittle2, "LittleFinger2_L" },
            { LeftLittle3, "LittleFinger3_L" },

            { RightThumb1, "Thumb0_R" },
            { RightThumb2, "Thumb1_R" },
            { RightFore1, "IndexFinger1_R" },
            { RightFore2, "IndexFinger2_R" },
            { RightFore3, "IndexFinger3_R" },
            { RightMiddle1, "MiddleFinger1_R" },
            { RightMiddle2, "MiddleFinger2_R" },
            { RightMiddle3, "MiddleFinger3_R" },
            { RightThird1, "RingFinger1_R" },
            { RightThird2, "RingFinger2_R" },
            { RightThird3, "RingFinger3_R" },
            { RightLittle1, "LittleFinger1_R" },
            { RightLittle2, "LittleFinger2_R" },
            { RightLittle3, "LittleFinger3_R" },

            { ArmIKLeft, "ArmL IK" },
            { ArmIKRight, "ArmR IK" },
            { ToeIKLeft, "ToeL IK" },
            { ToeIKRight, "ToeR IK" },
            { LeftFootIK, "FootL IK" },
            { RightFootIK, "FootR IK" },

            { ControlCenter, "Control Center" },           // 操作中心
            { RightEyeReset, "Right Eye Reset" },          // 右目戻
            { LeftEyeReset, "Left Eye Reset" },            // 左目戻
            { Glasses, "Glasses" },                        // メガネ
            { Tongue1, "Tongue 1" },                       // 舌１
            { Tongue2, "Tongue 2" },                       // 舌２
            { Tongue3, "Tongue 3" },                       // 舌３
            { RightArmTwist, "Right Arm Twist" },          // 右腕捩
            { RightArmTwist1, "Right Arm Twist 1" },       // 右腕捩1
            { RightArmTwist2, "Right Arm Twist 2" },       // 右腕捩2
            { RightArmTwist3, "Right Arm Twist 3" },       // 右腕捩3
            { RightHandTwist, "Right Hand Twist" },        // 右手捩
            { RightDummy, "Right Dummy" },                 // 右ダミー
            { RightThumb0, "Right Thumb 0" },              // 右親指０
            { RightHandTwist1, "Right Hand Twist 1" },     // 右手捩1
            { RightHandTwist2, "Right Hand Twist 2" },     // 右手捩2
            { RightHandTwist3, "Right Hand Twist 3" },     // 右手捩3
            { RightSleeve, "Right Sleeve" },               // 右袖
            { LeftArmTwist, "Left Arm Twist" },            // 左腕捩
            { LeftArmTwist1, "Left Arm Twist 1" },         // 左腕捩1
            { LeftArmTwist2, "Left Arm Twist 2" },         // 左腕捩2
            { LeftArmTwist3, "Left Arm Twist 3" },         // 左腕捩3
            { LeftHandTwist, "Left Hand Twist" },          // 左手捩
            { LeftDummy, "Left Dummy" },                   // 左ダミー
            { LeftThumb0, "Left Thumb 0" },                // 左親指０
            { LeftHandTwist1, "Left Hand Twist 1" },       // 左手捩1
            { LeftHandTwist2, "Left Hand Twist 2" },       // 左手捩2
            { LeftHandTwist3, "Left Hand Twist 3" },       // 左手捩3
            { LeftSleeve, "Left Sleeve" },                 // 左袖
            { NeckTie0, "Neck Tie 0" },                    // ﾈｸﾀｲ０
            { NeckTie1, "Neck Tie 1" },                    // ﾈｸﾀｲ１
            { NeckTie2, "Neck Tie 2" },                    // ﾈｸﾀｲ２
            { NeckTie3, "Neck Tie 3" },                    // ﾈｸﾀｲ３
            { NeckTie4, "Neck Tie 4" },                    // ﾈｸﾀｲ４
            { NeckTie4_, "Neck Tie 4_" },                  // ﾈｸﾀｲ４_
            { NeckTieIK, "Neck Tie IK" },                  // ﾈｸﾀｲＩＫ
            { WaistCancelRight, "Waist Cancel Right" },    // 腰キャンセル右
            { RightLegIKParent, "Right Leg IK Parent" },   // 右足IK親
            { Ankle_R_, "Ankle_R_" },                      // 足首_R_
            { WaistCancelLeft, "Waist Cancel Left" },      // 腰キャンセル左
            { LeftLegIKParent, "Left Leg IK Parent" },     // 左足IK親
            { Ankle_L_, "Ankle_L_" },                      // 足首_L_
            { RightLegD, "Right Leg D" },                  // 右足D
            { RightKneeD, "Right Knee D" },                // 右ひざD
            { RightAnkleD, "Right Ankle D" },              // 右足首D
            { RightToeEX, "Right Toe EX" },                // 右足先EX
            { LeftLegD, "Left Leg D" },                    // 左足D
            { LeftKneeD, "Left Knee D" },                  // 左ひざD
            { LeftAnkleD, "Left Ankle D" },                // 左足首D
            { LeftToeEX, "Left Toe EX" },                  // 左足先EX
            { NewBone1, "New Bone 1" },                    // 新規ボーン1
            { Skirt_0_0, "Skirt 0 0" },                    // スカート_0_0
            { Skirt_0_1, "Skirt 0 1" },                    // スカート_0_1
            { Skirt_0_2, "Skirt 0 2" },                    // スカート_0_2
            { Skirt_0_3, "Skirt 0 3" },                    // スカート_0_3
            { Skirt_0_4, "Skirt 0 4" },                    // スカート_0_4
            { Skirt_0_5, "Skirt 0 5" },                    // スカート_0_5
            { Skirt_0_6, "Skirt 0 6" },                    // スカート_0_6
            { Skirt_0_7, "Skirt 0 7" },                    // スカート_0_7
            { Skirt_1_0, "Skirt 1 0" },                    // スカート_1_0
            { Skirt_1_1, "Skirt 1 1" },                    // スカート_1_1
            { Skirt_1_2, "Skirt 1 2" },                    // スカート_1_2
            { Skirt_1_3, "Skirt 1 3" },                    // スカート_1_3
            { Skirt_1_4, "Skirt 1 4" },                    // スカート_1_4
            { Skirt_1_5, "Skirt 1 5" },                    // スカート_1_5
            { Skirt_1_6, "Skirt 1 6" },                    // スカート_1_6
            { Skirt_1_7, "Skirt 1 7" },                    // スカート_1_7
            { Skirt_2_0, "Skirt 2 0" },                    // スカート_2_0
            { Skirt_2_1, "Skirt 2 1" },                    // スカート_2_1
            { Skirt_2_2, "Skirt 2 2" },                    // スカート_2_2
            { Skirt_2_3, "Skirt 2 3" },                    // スカート_2_3
            { Skirt_2_4, "Skirt 2 4" },                    // スカート_2_4
            { Skirt_2_5, "Skirt 2 5" },                    // スカート_2_5
            { Skirt_2_6, "Skirt 2 6" },                    // スカート_2_6
            { Skirt_2_7, "Skirt 2 7" },                    // スカート_2_7
            { Skirt_3_0, "Skirt 3 0" },                    // スカート_3_0
            { Skirt_3_1, "Skirt 3 1" },                    // スカート_3_1
            { Skirt_3_2, "Skirt 3 2" },                    // スカート_3_2
            { Skirt_3_3, "Skirt 3 3" },                    // スカート_3_3
            { Skirt_3_4, "Skirt 3 4" },                    // スカート_3_4
            { Skirt_3_5, "Skirt 3 5" },                    // スカート_3_5
            { Skirt_3_6, "Skirt 3 6" },                    // スカート_3_6
            { Skirt_3_7, "Skirt 3 7" },                    // スカート_3_7
            { Belt1, "Belt 1" },                           // ベルト1
            { Belt, "Belt" },                              // ベルト
            { RightElbowHelper, "Right Elbow Helper" },    // 右ひじ補助
            { RightElbowHelper1, "Right Elbow Helper 1" }, // 右ひじ補助1
            { LeftElbowHelper, "Left Elbow Helper" },      // 左ひじ補助
            { LeftElbowHelper1, "Left Elbow Helper 1" },   // 左ひじ補助1
            { AdditionalRightElbowHelper, "Additional Right Elbow Helper" }, // +右ひじ補助
            { RightHandEnd, "Right Hand End" },            // 右手先
            { RightThumbEnd, "Right Thumb End" },          // 右親指先
            { RightLittleFingerEnd, "Right Little Finger End" }, // 右小指先
            { RightRingFingerEnd, "Right Ring Finger End" },     // 右薬指先
            { RightMiddleFingerEnd, "Right Middle Finger End" }, // 右中指先
            { RightIndexFingerEnd, "Right Index Finger End" },   // 右人指先
            { AdditionalLeftElbowHelper, "Additional Left Elbow Helper" }, // +左ひじ補助
            { LeftHandEnd, "Left Hand End" },              // 左手先
            { LeftThumbEnd, "Left Thumb End" },            // 左親指先
            { LeftLittleFingerEnd, "Left Little Finger End" }, // 左小指先
            { LeftRingFingerEnd, "Left Ring Finger End" }, // 左薬指先
            { LeftMiddleFingerEnd, "Left Middle Finger End" }, // 左中指先
            { LeftIndexFingerEnd, "Left Index Finger End" },   // 左人指先
            { StrayHair1, "Stray Hair 1" },                // アホ毛１
            { StrayHair2, "Stray Hair 2" },                // アホ毛２
            { RightSideburn1, "Right Sideburn 1" },        // 右もみあげ１
            { RightSideburn2, "Right Sideburn 2" },        // 右もみあげ２
            { LeftSideburn1, "Left Sideburn 1" },          // 左もみあげ１
            { LeftSideburn2, "Left Sideburn 2" },          // 左もみあげ２
            { FrontBangs1, "Front Bangs 1" },              // 前髪１
            { FrontBangs1_2, "Front Bangs 1_2" },          // 前髪１_２
            { FrontBangs2, "Front Bangs 2" },              // 前髪２
            { FrontBangs2_2, "Front Bangs 2_2" },          // 前髪２_２
            { FrontBangs3, "Front Bangs 3" },              // 前髪３
            { FrontBangs3_2, "Front Bangs 3_2" },          // 前髪３_２
            { RightSideburn1Shi, "Right Sideburn 1 Shi" }, // 右もみあげ１シ・
            { RightSideburn2Shi, "Right Sideburn 2 Shi" }, // 右もみあげ２シ・
            { LeftSideburn1Shi, "Left Sideburn 1 Shi" },   // 左もみあげ１シ・
            { LeftSideburn2Shi, "Left Sideburn 2 Shi" },   // 左もみあげ２シ・
            { BackHairMiddleShort, "Back Hair Middle Short" }, // 後ろ髪中ショ
            { BackHairRightShort, "Back Hair Right Short" },   // 後ろ髪右ショ
            { BackHairLeftShort, "Back Hair Left Short" },     // 後ろ髪左ショ
            { RightHair1, "Right Hair 1" },                // 右髪１
            { RightHair2, "Right Hair 2" },                // 右髪２
            { RightHair3, "Right Hair 3" },                // 右髪３
            { RightHair4, "Right Hair 4" },                // 右髪４
            { RightHair5, "Right Hair 5" },                // 右髪５
            { RightHair6, "Right Hair 6" },                // 右髪６
            { RightHair7, "Right Hair 7" },                // 右髪７
            { RightHair8, "Right Hair 8" },                // 右髪８
            { Hair8_R_, "Hair8_R_" },                      // 髪８_R_
            { RightHairIK, "Right Hair IK" },              // 右髪ＩＫ
            { LeftHair1, "Left Hair 1" },                  // 左髪１
            { LeftHair2, "Left Hair 2" },                  // 左髪２
            { LeftHair3, "Left Hair 3" },                  // 左髪３
            { LeftHair4, "Left Hair 4" },                  // 左髪４
            { LeftHair5, "Left Hair 5" },                  // 左髪５
            { LeftHair6, "Left Hair 6" },                  // 左髪６
            { LeftHair7, "Left Hair 7" },                  // 左髪７
            { LeftHair8, "Left Hair 8" },                  // 左髪８
            { Hair8_L_, "Hair8_L_" },                      // 髪８_L_
            { LeftHairIK, "Left Hair IK" },                // 左髪ＩＫ
            { IndicatorC1, "Indicator C1" },               // インジケータC1
            { IndicatorC2, "Indicator C2" },               // インジケータC2
            { IndicatorCG, "Indicator CG" },               // インジケータCG

            // Group 2
            { HideModel, "Hide Model" },                 // モデル消す
            { Blink, "Blink" },                          // まばたき
            { Laugh, "Laugh" },                          // 笑い
            { Wink, "Wink" },                            // ウィンク
            { WinkRight, "Wink Right" },                 // ウィンク右
            { Wink2, "Wink 2" },                         // ウィンク２
            { Wink2Right, "Wink 2 Right" },              // ｳｨﾝｸ２右
            { Calm, "Calm" },                            // なごみ
            { Hau, "Hau" },                              // はぅ
            { Surprised, "Surprised" },                  // びっくり
            { Glare, "Glare" },                          // じと目
            { Kiri, "Kiri" },                            // ｷﾘｯ
            { HachuEyes, "Hachu Eyes" },                // はちゅ目
            { HachuEyesVerticalSquash, "Hachu Eyes Vertical Squash" }, // はちゅ目縦潰れ
            { HachuEyesHorizontalSquash, "Hachu Eyes Horizontal Squash" }, // はちゅ目横潰れ
            { A, "A" },                                // あ
            { I, "I" },                                // い
            { U, "U" },                                // う
            { E, "E" },                                // え
            { O, "O" },                                // お
            { A2, "A2" },                              // あ２
            { N, "N" },                                // ん
            { Triangle, "Triangle" },                  // ▲
            { UpCaret, "UpCaret" },                    // ∧
            { Square, "Square" },                      // □
            { Wa, "Wa" },                              // ワ
            { Omega, "Omega" },                        // ω
            { OmegaSquare, "Omega Square" },           // ω□
            { Smirk, "Smirk" },                        // にやり
            { Smirk2, "Smirk 2" },                     // にやり２
            { Smiley, "Smiley" },                      // にっこり
            { TongueOut, "Tongue Out" },               // ぺろっ
            { Tehepero, "Tehepero" },                  // てへぺろ
            { Tehepero2, "Tehepero 2" },               // てへぺろ２
            { MouthCornerUp, "Mouth Corner Up" },      // 口角上げ
            { MouthCornerDown, "Mouth Corner Down" },  // 口角下げ
            { MouthWide, "Mouth Wide" },               // 口横広げ
            { NoTeethUp, "No Teeth Up" },              // 歯無し上
            { NoTeethDown, "No Teeth Down" },          // 歯無し下
            { Tears, "Tears" },                        // 涙
            { Outline, "Outline" },                    // 輪郭
            { StarEyes, "Star Eyes" },                 // 星目
            { Heart, "Heart" },                        // はぁと
            { SmallPupils, "Small Pupils" },           // 瞳小
            { PupilsVerticalSquash, "Pupils Vertical Squash" }, // 瞳縦潰れ
            { UnderLight, "Under Light" },             // 光下
            { TerrifyingChild, "Terrifying Child!" },  // 恐ろしい子！
            { Embarrassed, "Embarrassed" },            // 照れ
            { Gasp, "Gasp" },                          // がーん
            { Serious, "Serious" },                    // 真面目
            { Troubled, "Troubled" },                  // 困る
            { Smiling, "Smiling" },                    // にこり
            { Anger, "Anger" },                        // 怒り
            { Up, "Up" },                              // 上
            { Down, "Down" },                          // 下
            { Front, "Front" },                        // 前
            { LeftEyebrow, "Left Eyebrow" },           // 眉頭左
            { RightEyebrow, "Right Eyebrow" },         // 眉頭右
            // (メガネ duplicate is skipped)
            { TwinTeOn, "TwinTeon" },                  // ツインテon
            { HipRelativeOn, "Hip Relative On" },      // 腰相対on
            { GWarningOff, "G Warning Off" },          // G警告Off
            { CWarningOff, "C Warning Off" },          // C警告Off
            { FootGroundingOn, "Foot Grounding On" },  // 足接地On
            { IndicatorOff, "Indicator Off" }          // インジケ－タ消
        };
    }
}
