namespace XREngine.Editor
{
#if EDITOR
    public delegate void DelPropertyChange(EditorState state, string propertyValue, object oldValue, object newValue);
    public delegate void DelHighlightingChange(bool isHighlighted);
    public delegate void DelSelectedChange(bool isSelected);

    /// <summary>Tracks editor-only selection and property changes for an object.</summary>
    public class EditorState : TObject
    {
        public EditorState(IObject obj) => Object = obj;

        public static event DelPropertyChange PropertyChanged;
        public static event DelHighlightingChange HighlightingChanged;
        public static event DelSelectedChange SelectedChanged;

        private Dictionary<string, List<object>> _changedProperties = new Dictionary<string, List<object>>();
        private bool _highlighted = false;
        private bool _selected = false;

        private static EditorState _selectedState, _highlightedState;
        private TreeNode _treeNode;

        public static EditorState SelectedState
        {
            get => _selectedState;
            set
            {
                _selectedState?.OnSelectedChanged(false);
                _selectedState = value;
                _selectedState?.OnSelectedChanged(true);
            }
        }
        public static EditorState HighlightedState
        {
            get => _highlightedState;
            set
            {
                _highlightedState?.OnHighlightedChanged(false);
                _highlightedState = value;
                _highlightedState?.OnHighlightedChanged(true);
            }
        }

        //Contains information about the default value for each member.
        public Dictionary<string, object> MemberDefaults { get; set; }

        /// <summary>
        /// The object that this object in the world originates from.
        /// Can be used to reset object values to default.
        /// </summary>
        public IFileObject ReferenceObject { get; set; }

        public IObject Object { get; internal set; }
        public bool HasChanges => _changedProperties.Count > 0;
        public bool Highlighted
        {
            get => _highlighted;
            set
            {
                if (_highlighted == value)
                    return;
                HighlightedState = value ? this : null;
            }
        }
        public bool Selected
        {
            get => _selected;
            set
            {
                if (_selected == value)
                    return;
                SelectedState = value ? this : null;
            }
        }
        public TreeNode TreeNode
        {
            get => _treeNode;
            set
            {
                _treeNode = value;
                AppDomainHelper.Sponsor(_treeNode);
            }
        }

        /// <summary>
        /// List of all states that have been modified.
        /// </summary>
        public static List<EditorState> DirtyStates { get; } = new List<EditorState>();
        /// <summary>
        /// If set to false, this object will not be shown in the actor tree to the user for editing.
        /// </summary>
        public bool DisplayInActorTree { get; set; } = true;

        /// <summary>
        /// Returns true if there are any global changes that have been applied to this state.
        /// Indicates that this object should be saved.
        /// </summary>
        public bool IsDirty => _globalChanges.Count > 0;
        /// <summary>
        /// All changes made in a global scope that affect the object this state is monitoring.
        /// </summary>
        public IReadOnlyList<GlobalChange> GlobalChanges => _globalChanges;
        private List<GlobalChange> _globalChanges = new List<GlobalChange>();

        internal void RemoveGlobalChange(GlobalChange change)
        {
            lock (_globalChanges)
            {
                _globalChanges.Remove(change);
                if (_globalChanges.Count == 0)
                    DirtyStates.Remove(this);
            }
        }
        internal void AddGlobalChange(GlobalChange change)
        {
            lock (_globalChanges)
            {
                if (!_globalChanges.Contains(change))
                    _globalChanges.Add(change);
                if (_globalChanges.Count > 0 && !DirtyStates.Contains(this))
                    DirtyStates.Add(this);
            }
        }

        private new void OnSelectedChanged(bool selected)
        {
            if (Object == this)
                return;

            _selected = selected;
            Object?.OnSelectedChanged(_selected);
            SelectedChanged?.Invoke(_selected);
        }
        private void OnHighlightedChanged(bool highlighted)
        {
            if (Object == this)
                return;

            _highlighted = highlighted;
            Object?.OnHighlightChanged(_highlighted);
            HighlightingChanged?.Invoke(highlighted);
        }
        public GlobalChange AddChanges(params LocalValueChange[] changes)
        {
            GlobalChange globalChange = new GlobalChange
            {
                ChangedStates = new ConcurrentBag<(EditorState State, LocalValueChange Change)>()
            };
            foreach (LocalValueChange change in changes)
            {
                globalChange.ChangedStates.Add((this, change));
                change.GlobalChange = globalChange;
            }
            AddGlobalChange(globalChange);
            return globalChange;
        }

        public void ClearChanges()
        {
            lock (_globalChanges)
            {
                _globalChanges.Clear();
                DirtyStates.Remove(this);
            }
        }

        //public void AddChange(object oldValue, object newValue, IList list, int index, GlobalValueChange change)
        //{
        //    ChangedValues.Add(new ListValueChange()
        //    {
        //        GlobalChange = change,
        //        OldValue = oldValue,
        //        NewValue = newValue,
        //        List = list,
        //        Index = index,
        //    });
        //    IsDirty = true;
        //}
        //public void AddChange(object oldValue, object newValue, object propertyOwner, PropertyInfo propertyInfo, GlobalValueChange change)
        //{
        //    ChangedValues.Add(new PropertyValueChange()
        //    {
        //        GlobalChange = change,
        //        OldValue = oldValue,
        //        NewValue = newValue,
        //        PropertyOwner = propertyOwner,
        //        PropertyInfo = propertyInfo,
        //    });
        //    IsDirty = true;
        //}
        //public void AddChange(object oldValue, object newValue, IDictionary dicOwner, object key, bool isKey, GlobalValueChange change)
        //{
        //    ChangedValues.Add(new DictionaryValueChange()
        //    {
        //        GlobalChange = change,
        //        OldValue = oldValue,
        //        NewValue = newValue,
        //        DictionaryOwner = dicOwner,
        //        Key = key,
        //        IsKey = isKey,
        //    });
        //    IsDirty = true;
        //}

        //private static Dictionary<int, StencilTest> 
        //    _highlightedMaterials = new Dictionary<int, StencilTest>(), 
        //    _selectedMaterials = new Dictionary<int, StencilTest>();
        internal static void RegisterHighlightedMaterial(XRMaterial m, bool highlighted, IScene scene)
        {
            //if (m is null)
            //    return;
            //if (highlighted)
            //{
            //    if (_highlightedMaterials.ContainsKey(m.UniqueID))
            //    {
            //        //m.RenderParams.StencilTest.BackFace.Ref |= 1;
            //        //m.RenderParams.StencilTest.FrontFace.Ref |= 1;
            //        return;
            //    }
            //    _highlightedMaterials.Add(m.UniqueID, m.RenderParams.StencilTest);
            //    m.RenderParams.StencilTest = OutlinePassStencil;
            //}
            //else
            //{
            //    if (!_highlightedMaterials.ContainsKey(m.UniqueID))
            //    {
            //        //m.RenderParams.StencilTest.BackFace.Ref &= ~1;
            //        //m.RenderParams.StencilTest.FrontFace.Ref &= ~1;
            //        return;
            //    }
            //    StencilTest t = _highlightedMaterials[m.UniqueID];
            //    _highlightedMaterials.Remove(m.UniqueID);
            //    m.RenderParams.StencilTest = _selectedMaterials.ContainsKey(m.UniqueID) ? _selectedMaterials[m.UniqueID] : t;
            //}
        }

        public static void RegisterSelectedMesh(XRMaterial m, bool selected, IScene scene)
        {
            //if (m is null)
            //    return;
            //if (selected)
            //{
            //    if (_selectedMaterials.ContainsKey(m.UniqueID))
            //    {
            //        //m.RenderParams.StencilTest.BackFace.Ref |= 2;
            //        //m.RenderParams.StencilTest.FrontFace.Ref |= 2;
            //        return;
            //    }
            //    else
            //    {
            //        _selectedMaterials.Add(m.UniqueID, m.RenderParams.StencilTest);
            //        m.RenderParams.StencilTest = OutlinePassStencil;
            //        //m.RenderParams.StencilTest.BackFace.Ref |= 2;
            //        //m.RenderParams.StencilTest.FrontFace.Ref |= 2;
            //    }
            //}
            //else
            //{
            //    if (!_selectedMaterials.ContainsKey(m.UniqueID))
            //    {
            //        //m.RenderParams.StencilTest.BackFace.Ref &= ~2;
            //        //m.RenderParams.StencilTest.FrontFace.Ref &= ~2;
            //        return;
            //    }
            //    StencilTest t = _selectedMaterials[m.UniqueID];
            //    _selectedMaterials.Remove(m.UniqueID);
            //    m.RenderParams.StencilTest = t;
            //}
        }

        //public static StencilTest NormalPassStencil = new StencilTest()
        //{
        //    Enabled = ERenderParamUsage.Enabled,
        //    //BothFailOp = EStencilOp.Keep,
        //    //StencilPassDepthFailOp = EStencilOp.Keep,
        //    //BothPassOp = EStencilOp.Replace,
        //    BackFace = new StencilTestFace()
        //    {
        //        Func = EComparison.Always,
        //        Ref = 0,
        //        WriteMask = 0,
        //        ReadMask = 0,
        //    },
        //    FrontFace = new StencilTestFace()
        //    {
        //        Func = EComparison.Always,
        //        Ref = 0,
        //        WriteMask = 0,
        //        ReadMask = 0,
        //    },
        //};
        //public static StencilTest OutlinePassStencil = new StencilTest()
        //{
        //    Enabled = ERenderParamUsage.Enabled,
        //    BackFace = new StencilTestFace()
        //    {
        //        BothFailOp = EStencilOp.Keep,
        //        StencilPassDepthFailOp = EStencilOp.Replace,
        //        BothPassOp = EStencilOp.Replace,
        //        Func = EComparison.Always,
        //        Ref = 1,
        //        WriteMask = 0xFF,
        //        ReadMask = 0xFF,
        //    },
        //    FrontFace = new StencilTestFace()
        //    {
        //        BothFailOp = EStencilOp.Keep,
        //        StencilPassDepthFailOp = EStencilOp.Replace,
        //        BothPassOp = EStencilOp.Replace,
        //        Func = EComparison.Always,
        //        Ref = 1,
        //        WriteMask = 0xFF,
        //        ReadMask = 0xFF,
        //    },
        //};
        //public static XRMaterial FocusedMeshMaterial;
        //private static void M_PreRendered(BaseRenderableMesh mesh, Matrix4 matrix, Matrix3 normalMatrix, XRMaterial material, BaseRenderableMesh.PreRenderCallback callback)
        //{
        //    callback.ShouldRender = false;
        //    XRMaterial m = mesh.CurrentLOD.Manager.GetRenderMaterial(material);
        //    StencilTest prev = m.RenderParams.StencilTest;
        //    m.RenderParams.StencilTest = NormalPassStencil;
        //    mesh.Render(m, false, false);
        //    mesh.Render(FocusedMeshMaterial, false, false);
        //    m.RenderParams.StencilTest = prev;
        //}
        //static EditorState()
        //{
        //    FocusedMeshMaterial = XRMaterial.CreateLitColorMaterial(Color.Yellow, true);
        //    FocusedMeshMaterial.AddShader(Engine.LoadEngineShader("StencilExplode.gs", EShaderMode.Geometry));
        //    FocusedMeshMaterial.RenderParams.StencilTest = OutlinePassStencil;
        //    FocusedMeshMaterial.RenderParams.DepthTest.Enabled = ERenderParamUsage.Disabled;
        //}
    }
    public class EngineEditorState : TObjectSlim
    {
        /// <summary>
        /// Used to determine if the editor is editing the game currently instead of simulating gameplay.
        /// </summary>
        public bool InEditMode { get; set; } = true;
        public ICameraComponent PinnedCameraComponent { get; set; }
    }

    /// <summary>
    /// Contains information pertaining to a change in a global setting.
    /// </summary>
    public class GlobalChange : TObjectSlim
    {
        public ConcurrentBag<(EditorState State, LocalValueChange Change)> ChangedStates { get; set; }
        //public EditorState State { get; set; }
        //public int ChangeIndex { get; set; }

        public void ApplyNewValue()
        {
            foreach (var (State, Change) in ChangedStates)
            {
                try
                {
                    Change?.ApplyNewValue();
                }
                catch (Exception ex)
                {
                    Engine.LogException(ex);
                }
                try
                {
                    State?.AddGlobalChange(this);
                }
                catch (Exception ex)
                {
                    Engine.LogException(ex);
                }
            }
        }
        public void ApplyOldValue()
        {
            foreach (var (State, Change) in ChangedStates)
            {
                try
                {
                    Change?.ApplyOldValue();
                }
                catch (Exception ex)
                {
                    Engine.LogException(ex);
                }
                try
                {
                    State?.AddGlobalChange(this);
                }
                catch (Exception ex)
                {
                    Engine.LogException(ex);
                }
            }
        }

        public void DestroySelf()
        {
            //Unlink from local editor states
            var modifiedStates = ChangedStates.Select(x => x.State).Distinct();
            foreach (var state in modifiedStates)
            {
                try
                {
                    state?.RemoveGlobalChange(this);
                }
                catch (Exception ex)
                {
                    Engine.LogException(ex);
                }
            }

            //for (int i = 0; i < ChangedStates.Count; ++i)
            //{
            //    var (State, ChangeIndex) = ChangedStates[i];

            //    State.ChangedValues.RemoveAt(ChangeIndex);

            //    //Update all local changes after the one that was just removed
            //    //Their global state's change index needs to be decremented to match the new index
            //    for (int x = ChangeIndex; x < State.ChangedValues.Count; ++x)
            //        --ChangeIndex;

            //    if (State.ChangedValues.Count == 0)
            //        State.IsDirty = false;
            //}
        }

        public string AsUndoString()
        {
            string s = "";
            foreach (var (State, Change) in ChangedStates)
            {
                if (s.Length > 0)
                    s += ", ";
                s += $"({Change.DisplayChangeAsUndo()}";
            }
            return s;
        }
        public string AsRedoString()
        {
            string s = "";
            foreach (var (State, Change) in ChangedStates)
            {
                if (s.Length > 0)
                    s += ", ";
                s += $"({Change.DisplayChangeAsRedo()}";
            }
            return s;
        }
        public override string ToString() => AsRedoString();
    }
    /// <summary>
    /// Contains information pertaining to a change on a specific object.
    /// </summary>
    public abstract class LocalValueChange : TObjectSlim
    {
        public LocalValueChange(object oldValue, object newValue)
        {
            OldValue = oldValue;
            NewValue = newValue;
        }

        public GlobalChange GlobalChange { get; set; }
        public object OldValue { get; set; }
        public object NewValue { get; set; }

        public abstract void ApplyNewValue();
        public abstract void ApplyOldValue();
        public abstract string DisplayChangeAsUndo();
        public abstract string DisplayChangeAsRedo();

        public override string ToString() => DisplayChangeAsRedo();
    }
    public class LocalValueChangeIList : LocalValueChange
    {
        public LocalValueChangeIList(object oldValue, object newValue, IList list, int index) : base(oldValue, newValue)
        {
            List = list;
            Index = index;
        }

        public IList List { get; set; }
        public int Index { get; set; }

        public override void ApplyNewValue()
            => List[Index] = NewValue;
        public override void ApplyOldValue()
            => List[Index] = OldValue;

        public override string DisplayChangeAsRedo()
        {
            return string.Format("{0}[{1}] {2} -> {3}",
                List.ToString(), Index.ToString(),
              OldValue?.ToString() ?? "null", NewValue?.ToString() ?? "null");
        }

        public override string DisplayChangeAsUndo()
        {
            return string.Format("{0}[{1}] {2} <- {3}",
                List.ToString(), Index.ToString(),
                OldValue?.ToString() ?? "null", NewValue?.ToString() ?? "null");
        }
    }
    [Serializable]
    public class LocalValueChangeField : LocalValueChange
    {
        public LocalValueChangeField(object oldValue, object newValue, object fieldOwner, FieldInfo fieldInfo) : base(oldValue, newValue)
        {
            FieldOwner = fieldOwner;
            FieldInfo = fieldInfo;
        }

        public object FieldOwner { get; set; }
        public FieldInfo FieldInfo { get; set; }

        public override void ApplyNewValue()
            => FieldInfo.SetValue(FieldOwner, NewValue);
        public override void ApplyOldValue()
            => FieldInfo.SetValue(FieldOwner, OldValue);

        public override string DisplayChangeAsRedo()
            => string.Format("{0}.{1} {2} -> {3}",
              FieldOwner.ToString(), FieldInfo.Name.ToString(),
              OldValue is null ? "null" : OldValue.ToString(),
              NewValue is null ? "null" : NewValue.ToString());

        public override string DisplayChangeAsUndo()
            => string.Format("{0}.{1} {2} <- {3}",
              FieldOwner.ToString(), FieldInfo.Name.ToString(),
              OldValue is null ? "null" : OldValue.ToString(),
              NewValue is null ? "null" : NewValue.ToString());
    }
    [Serializable]
    public class LocalValueChangeProperty : LocalValueChange
    {
        public LocalValueChangeProperty(object oldValue, object newValue, object propertyOwner, PropertyInfoProxy propertyInfo) : base(oldValue, newValue)
        {
            PropertyOwner = propertyOwner;
            PropertyInfo = propertyInfo;
        }

        public object PropertyOwner { get; set; }
        public PropertyInfoProxy PropertyInfo { get; set; }

        public override void ApplyNewValue()
            => PropertyInfo.SetValue(PropertyOwner, NewValue);
        public override void ApplyOldValue()
            => PropertyInfo.SetValue(PropertyOwner, OldValue);

        public override string DisplayChangeAsRedo()
            => string.Format("{0}.{1} {2} -> {3}",
              PropertyOwner.ToString(), PropertyInfo.Name.ToString(),
              OldValue is null ? "null" : OldValue.ToString(),
              NewValue is null ? "null" : NewValue.ToString());

        public override string DisplayChangeAsUndo()
            => string.Format("{0}.{1} {2} <- {3}",
              PropertyOwner.ToString(), PropertyInfo.Name.ToString(),
              OldValue is null ? "null" : OldValue.ToString(),
              NewValue is null ? "null" : NewValue.ToString());
    }
    [Serializable]
    public class LocalValueChangeIDictionary : LocalValueChange
    {
        public IDictionary DictionaryOwner { get; set; }
        public object KeyForValue { get; set; }
        public bool ValueIsKey { get; set; }

        public LocalValueChangeIDictionary(object oldValue, object newValue, IDictionary dictionary, object keyForValue, bool valueIsKey) : base(oldValue, newValue)
        {
            DictionaryOwner = dictionary;
            KeyForValue = keyForValue;
            ValueIsKey = valueIsKey;
        }

        //TODO: handle key add/removes

        public override void ApplyNewValue()
        {
            if (!ValueIsKey)
                DictionaryOwner[KeyForValue] = NewValue;
            else
            {
                if (!DictionaryOwner.Contains(OldValue))
                    return;
                object value = DictionaryOwner[OldValue];
                DictionaryOwner.Remove(OldValue);
                if (DictionaryOwner.Contains(NewValue))
                    DictionaryOwner[NewValue] = value;
                else
                    DictionaryOwner.Add(NewValue, value);
            }
        }
        public override void ApplyOldValue()
        {
            if (!ValueIsKey)
                DictionaryOwner[KeyForValue] = OldValue;
            else
            {
                if (!DictionaryOwner.Contains(NewValue))
                    return;
                object value = DictionaryOwner[NewValue];
                DictionaryOwner.Remove(NewValue);
                if (DictionaryOwner.Contains(OldValue))
                    DictionaryOwner[OldValue] = value;
                else
                    DictionaryOwner.Add(OldValue, value);
            }
        }

        public override string DisplayChangeAsRedo()
            => string.Format("{0}.{1} {2} -> {3}",
              DictionaryOwner.ToString(), KeyForValue.ToString(),
              OldValue is null ? "null" : OldValue.ToString(),
              NewValue is null ? "null" : NewValue.ToString());

        public override string DisplayChangeAsUndo()
            => string.Format("{0}.{1} {2} <- {3}",
              DictionaryOwner.ToString(), KeyForValue.ToString(),
              OldValue is null ? "null" : OldValue.ToString(),
              NewValue is null ? "null" : NewValue.ToString());
    }
#endif
}
