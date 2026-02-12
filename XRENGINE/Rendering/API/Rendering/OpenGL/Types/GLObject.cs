namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        /// <summary>
        /// Generic OpenGL object base class for specific derived generic render objects.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        public abstract class GLObject<T> : GLObjectBase where T : GenericRenderObject
        {
            //We want to set the property instead of the field here just in case subclasses override it.
            //It will never be set to null because the constructor requires a non-null value.
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
            public GLObject(OpenGLRenderer renderer, T data) : base(renderer)
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
            {
                ArgumentNullException.ThrowIfNull(data);

                _data = data;
                _data.AddWrapper(this);
                LinkData();
            }

            protected override GenericRenderObject Data_Internal => Data;

            public override string GetDescribingName()
                => $"{GetType().Name} {(TryGetBindingId(out uint id) ? id.ToString() : "<Ungenerated>")}{(Data.Name is not null ? $" '{Data.Name}'" : "")}";

            private T _data;
            public T Data
            {
                get => _data;
                protected set
                {
                    if (value == _data)
                        return;

                    if (_data is not null)
                    {
                        UnlinkData();
                        _data.RemoveWrapper(this);
                    }

                    _data = value;

                    if (_data is not null)
                    {
                        _data.AddWrapper(this);
                        LinkData();
                    }
                }
            }

            protected abstract void UnlinkData();
            protected abstract void LinkData();

            protected override uint CreateObject()
            {
                uint id = base.CreateObject();
                if (id > 0)
                {
                    if (Cache.ContainsKey(id))
                    {
                        Debug.OpenGLWarning($"OpenGL {Type} object with binding id {id} already exists in cache.");
                        Cache[id] = this;
                    }
                    else
                        Cache.Add(id, this);
                }
                else
                    Debug.OpenGLWarning($"Failed to generate OpenGL {Type} object.");
                return id;
            }
            protected override void DeleteObject()
            {
                if (TryGetBindingId(out var bindingId))
                    Cache.Remove(bindingId);

                base.DeleteObject();
            }

            public static EventDictionary<uint, GLObject<T>> Cache { get; } = [];
        }
    }
}