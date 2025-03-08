using Extensions;
using System.Drawing;
using XREngine.Components.Scene.Shapes;
using XREngine.Data.Core;
using XREngine.Physics;
using XREngine.Physics.ContactTesting;

namespace XREngine.Components.Scene.Volumes
{
    public delegate void DelOnOverlapEnter(XRCollisionObject actor);
    public delegate void DelOnOverlapLeave(XRCollisionObject actor);
    public class TriggerVolumeComponent : BoxComponent
    {
        public DelOnOverlapEnter Entered;
        public DelOnOverlapLeave Left;

        public bool TrackContacts { get; set; } = false;

        protected virtual void OnEntered(XRCollisionObject obj) => Entered?.Invoke(obj);
        protected virtual void OnLeft(XRCollisionObject obj) => Left?.Invoke(obj);

        public class TriggerContactInfo : XRBase
        {
            public TriggerContactInfo() { }
            public TriggerContactInfo(XRContactInfo contact, bool isB)
            {
                Contact = contact;
                IsObjectB = isB;
            }

            public XRContactInfo Contact { get; set; }
            public bool IsObjectB { get; set; }
        }

        public Dictionary<XRCollisionObject, TriggerContactInfo?> Contacts { get; } = [];

        //public TriggerVolumeComponent() : this(1.0f) { }
        //public TriggerVolumeComponent(Vector3 halfExtents)
        //    : base(halfExtents, new TGhostBodyConstructionInfo()
        //    {
        //        CollidesWith = (ushort)ECollisionGroup.DynamicWorld,
        //        CollisionGroup = (ushort)ECollisionGroup.StaticWorld,
        //        CollisionEnabled = false,
        //        SimulatePhysics = false,
        //    }) { }

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            RegisterTick(ETickGroup.PostPhysics, (int)ETickOrder.Scene, Tick);
        }
        protected internal override void OnComponentDeactivated()
        {
            UnregisterTick(ETickGroup.PostPhysics, (int)ETickOrder.Scene, Tick);
            base.OnComponentDeactivated();
        }

        private readonly ContactTestMulti _test = new(null, 0, 0);
        private void Tick()
        {
            //if (CollisionObject is not TGhostBody ghost)
            //    return;

            if (TrackContacts)
            {
                ushort group = CollisionObject.CollisionGroup;
                ushort with = CollisionObject.CollidesWith;

                _test.Object = CollisionObject;
                _test.CollisionGroup = group;
                _test.CollidesWith = with;
                _test.Test(World);

                List<XRCollisionObject> remove = new List<XRCollisionObject>(Contacts.Count);
                foreach (var kv in Contacts)
                {
                    int matchIndex = _test.Results.FindIndex(x => x.CollisionObject == kv.Key);
                    if (_test.Results.IndexInRange(matchIndex))
                    {
                        var match = _test.Results[matchIndex];
                        _test.Results.RemoveAt(matchIndex);

                        kv.Value.Contact = match.Contact;
                        kv.Value.IsObjectB = match.IsObjectB;
                    }
                    else
                    {
                        remove.Add(kv.Key);
                    }
                }
                foreach (var obj in remove)
                {
                    Contacts.Remove(obj);
                    OnLeft(obj);
                    Debug.Out($"TRIGGER OBJECT LEFT: {Contacts.Count} contacts total");
                }
                foreach (var result in _test.Results)
                {
                    var info = new TriggerContactInfo(result.Contact, result.IsObjectB);
                    var obj = result.CollisionObject;

                    if (!Contacts.TryAdd(obj, info))
                        Contacts[obj] = info;
                    else
                    {
                        OnEntered(obj);
                        Debug.Out($"TRIGGER OBJECT ENTERED: {Contacts.Count} contacts total");
                    }
                    //RigidBodyCollision.OnOverlapped(result.CollisionObject, result.Contact, result.IsObjectB);
                    //result.CollisionObject.OnOverlapped(RigidBodyCollision, result.Contact, !result.IsObjectB);
                }
            }
            else
            {
                //var list = ghost.CollectOverlappingPairs();
                //List<XRCollisionObject> remove = new List<XRCollisionObject>(Contacts.Count);
                //foreach (var kv in Contacts)
                //{
                //    int matchIndex = list.IndexOf(kv.Key);
                //    if (list.IndexInRange(matchIndex))
                //    {
                //        var match = list[matchIndex];
                //        list.RemoveAt(matchIndex);
                //    }
                //    else
                //    {
                //        remove.Add(kv.Key);
                //    }
                //}
                //foreach (var obj in remove)
                //{
                //    Contacts.Remove(obj);
                //    OnLeft(obj);
                //    Debug.Out($"TRIGGER OBJECT LEFT: {Contacts.Count} contacts total");
                //}
                //foreach (var obj in list)
                //{
                //    if (Contacts.ContainsKey(obj))
                //        Contacts[obj] = null;
                //    else
                //    {
                //        Contacts.Add(obj, null);
                //        OnEntered(obj);
                //        Debug.Out($"TRIGGER OBJECT ENTERED: {Contacts.Count} contacts total");
                //    }
                //    //RigidBodyCollision.OnOverlapped(result.CollisionObject, result.Contact, result.IsObjectB);
                //    //result.CollisionObject.OnOverlapped(RigidBodyCollision, result.Contact, !result.IsObjectB);
                //}
            }
        }

        protected override void Render()
        {
            base.Render();

            if (!TrackContacts)
                return;

            var contacts = Contacts.ToList();
            foreach (var contact in contacts)
            {
                var contactInfo = contact.Value;
                if (contactInfo is null || contact.Value is null)
                    continue;

                var aPos = contact.Value.Contact.PositionWorldOnA;
                var bPos = contact.Value.Contact.PositionWorldOnB;

                Engine.Rendering.Debug.RenderPoint(aPos, Color.Red);
                Engine.Rendering.Debug.RenderPoint(bPos, Color.Green);
                Engine.Rendering.Debug.RenderLine(aPos, bPos, Color.Magenta);
            }
        }
    }
}
