//using Silk.NET.Vulkan;
//using Buffer = Silk.NET.Vulkan.Buffer;

//namespace XREngine.Rendering.Vulkan;
//public unsafe partial class VulkanRenderer
//{
//    public class VkBuffer(VulkanRenderer api, XRDataBuffer data) : VkObject<XRDataBuffer>(api, data)
//    {
//        private Buffer _buffer = default;
//        private DeviceMemory _memory = default;
//        private ulong _count;

//        public bool AllowResize { get; set; } = false;
//        public Buffer Buffer
//        {
//            get => _buffer;
//            set => _buffer = value;
//        }
//        public DeviceMemory Memory
//        {
//            get => _memory;
//            set => _memory = value; 
//        }

//        public ulong Count
//        {
//            get => _count;
//            set
//            {
//                if (_count == value)
//                    return;

//                if (AllowResize)
//                {
//                    Destroy();
//                    _count = value;
//                    Generate();
//                }
//                else
//                    throw new InvalidOperationException($"This buffer cannot be resized. {_count} -> {value}");
//            }
//        }
//        public BufferUsageFlags Usage { get; set; } = BufferUsageFlags.TransferSrcBit;
//        public MemoryPropertyFlags Properties { get; set; } = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
//        public SharingMode Sharing { get; set; } = SharingMode.Exclusive;
//        public BufferCreateFlags Flags { get; set; } = BufferCreateFlags.None;

//        public override void Generate()
//        {
//            BufferCreateInfo bufferInfo = new()
//            {
//                SType = StructureType.BufferCreateInfo,
//                Size = Size,
//                Usage = Usage,
//                SharingMode = Sharing,
//                Flags = Flags,
//            };

//            fixed (Buffer* bufferPtr = &_buffer)
//            {
//                if (Api.CreateBuffer(Renderer.device, ref bufferInfo, null, bufferPtr) != Result.Success)
//                    throw new Exception("Failed to create buffer.");
//            }

//            MemoryRequirements memRequirements = new();
//            Api.GetBufferMemoryRequirements(Renderer.device, _buffer, out memRequirements);

//            MemoryAllocateInfo allocInfo = new()
//            {
//                SType = StructureType.MemoryAllocateInfo,
//                AllocationSize = memRequirements.Size,
//                MemoryTypeIndex = Renderer.FindMemoryType(memRequirements.MemoryTypeBits, Properties),
//            };

//            fixed (DeviceMemory* memPtr = &_memory)
//            {
//                Renderer.AllocateMemory(allocInfo, memPtr);
//            }

//            Api!.BindBufferMemory(Renderer.device, _buffer, _memory, 0);
//        }

//        public override void Destroy()
//        {
//            Api!.DestroyBuffer(Renderer.device, _buffer, null);
//            Api!.FreeMemory(Renderer.device, _memory, null);
//        }

//        public Span<T> BeginUpdate()
//        {
//            void* data;
//            Api!.MapMemory(Renderer.device, Memory, 0, Size, 0, &data);
//            return new Span<T>(data, (int)Size);
//        }

//        public void EndUpdate()
//            => Api!.UnmapMemory(Renderer.device, Memory);

//        public void Set(int startIndex, params T[] items)
//        {
//            var span = BeginUpdate();
//            for (int i = 0; i < items.Length; i++)
//                span[startIndex + i] = items[i];
//            EndUpdate();
//        }
//        public void Set(int startIndex, IEnumerable<T> items)
//        {
//            var span = BeginUpdate();
//            int i = 0;
//            foreach (var item in items)
//                span[startIndex + i++] = item;
//            EndUpdate();
//        }

//        public void CopyTo(VkBuffer<T> other)
//        {
//            using var scope = Renderer.NewCommandScope();
//            BufferCopy copyRegion = new() { Size = Size };
//            Api!.CmdCopyBuffer(scope.CommandBuffer, Buffer, other.Buffer, 1, ref copyRegion);
//        }

//        protected override uint CreateObjectInternal()
//        {
//            throw new NotImplementedException();
//        }

//        protected override void DeleteObjectInternal()
//        {
//            throw new NotImplementedException();
//        }
//    }
//}