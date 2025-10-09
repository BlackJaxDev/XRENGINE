using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private CommandBuffer[]? _commandBuffers;

        private void DestroyCommandBuffers()
        {
            fixed (CommandBuffer* commandBuffersPtr = _commandBuffers)
            {
                Api!.FreeCommandBuffers(device, commandPool, (uint)_commandBuffers!.Length, commandBuffersPtr);
            }
        }

        private void CreateCommandBuffers()
        {
            _commandBuffers = new CommandBuffer[swapChainFramebuffers!.Length];

            CommandBufferAllocateInfo allocInfo = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = (uint)_commandBuffers.Length,
            };

            fixed (CommandBuffer* commandBuffersPtr = _commandBuffers)
            {
                if (Api!.AllocateCommandBuffers(device, ref allocInfo, commandBuffersPtr) != Result.Success)
                    throw new Exception("Failed to allocate command buffers.");
            }

            for (int i = 0; i < _commandBuffers.Length; i++)
                RunCommand(i, _commandBuffers[i]);
        }

        private void RunCommand(int i, CommandBuffer cmd)
        {
            CommandBufferBeginInfo beginInfo = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
            };

            if (Api!.BeginCommandBuffer(cmd, ref beginInfo) != Result.Success)
                throw new Exception("Failed to begin recording command buffer.");

            RenderPassBeginInfo renderPassInfo = new()
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = _renderPass,
                Framebuffer = swapChainFramebuffers![i],
                RenderArea =
                {
                    Offset = { X = 0, Y = 0 },
                    Extent = swapChainExtent,
                }
            };

            var clearValues = new ClearValue[]
            {
                new()
                {
                    Color = new (){ Float32_0 = 0, Float32_1 = 0, Float32_2 = 0, Float32_3 = 1 },
                },
                new()
                {
                    DepthStencil = new () { Depth = 1, Stencil = 0 }
                }
            };

            fixed (ClearValue* clearValuesPtr = clearValues)
            {
                renderPassInfo.ClearValueCount = (uint)clearValues.Length;
                renderPassInfo.PClearValues = clearValuesPtr;

                Api!.CmdBeginRenderPass(cmd, &renderPassInfo, SubpassContents.Inline);
            }

            //_testModel?.Draw(cmd, descriptorSets![i]);

            Api!.CmdEndRenderPass(cmd);

            if (Api!.EndCommandBuffer(cmd) != Result.Success)
                throw new Exception("Failed to record command buffer.");
        }

        public class CommandScope(VulkanRenderer api, CommandBuffer cmd) : IDisposable
        {
            public CommandBuffer CommandBuffer => cmd;

            public void Dispose()
            {
                api.CommandsStop(CommandBuffer);
                GC.SuppressFinalize(this);
            }
        }

        /// <summary>
        /// Allocates a new command buffer scope.
        /// </summary>
        /// <returns></returns>
        private CommandScope NewCommandScope()
            => new(this, CommandsStart());

        /// <summary>
        /// Starts a new set of commands to execute.
        /// </summary>
        /// <returns></returns>
        private CommandBuffer CommandsStart()
        {
            CommandBufferAllocateInfo allocateInfo = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                Level = CommandBufferLevel.Primary,
                CommandPool = commandPool,
                CommandBufferCount = 1,
            };

            Api!.AllocateCommandBuffers(device, ref allocateInfo, out CommandBuffer commandBuffer);

            CommandBufferBeginInfo beginInfo = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };

            Api!.BeginCommandBuffer(commandBuffer, ref beginInfo);

            return commandBuffer;
        }

        /// <summary>
        /// Finishes the current set of commands and submits them to the graphics queue.
        /// </summary>
        /// <param name="commandBuffer"></param>
        private void CommandsStop(CommandBuffer commandBuffer)
        {
            Api!.EndCommandBuffer(commandBuffer);

            SubmitInfo submitInfo = new()
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer,
            };

            Api!.QueueSubmit(graphicsQueue, 1, ref submitInfo, default);
            Api!.QueueWaitIdle(graphicsQueue);

            Api!.FreeCommandBuffers(device, commandPool, 1, ref commandBuffer);
        }
    }
}