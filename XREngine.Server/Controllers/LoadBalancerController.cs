using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using XREngine.Networking.LoadBalance;

namespace XREngine.Networking.Controllers
{
    [ApiController]
    [Route("api/load-balancer")]
    public class LoadBalancerController(LoadBalancerService service) : ControllerBase
    {
        private readonly LoadBalancerService _service = service;

        [HttpPost("register")]
        public ActionResult<ServerStatusResponse> Register([FromBody] RegisterRequest request)
        {
            if (request.Port <= 0)
                return BadRequest("Port must be greater than zero.");

            var ip = string.IsNullOrWhiteSpace(request.IpAddress)
                ? HttpContext.Connection.RemoteIpAddress?.ToString()
                : request.IpAddress;
            if (string.IsNullOrWhiteSpace(ip))
                return BadRequest("IP address is required.");

            var server = new Server
            {
                Id = request.ServerId ?? Guid.NewGuid(),
                IP = ip,
                Region = request.Region,
                Port = request.Port,
                MaxPlayers = Math.Max(1, request.MaxPlayers),
                CurrentLoad = Math.Max(0, request.CurrentLoad),
                Instances = request.Instances?.Distinct().ToList() ?? []
            };

            var status = _service.RegisterOrUpdate(server);
            return Ok(ServerStatusResponse.From(status));
        }

        [HttpPost("heartbeat")]
        public IActionResult Heartbeat([FromBody] HeartbeatRequest request)
        {
            if (_service.Heartbeat(request.ServerId, request.CurrentLoad, request.MaxPlayers, request.Instances))
                return Ok();

            return NotFound(new { message = "Server not registered." });
        }

        [HttpPost("claim")]
        public ActionResult<ServerStatusResponse> Claim([FromBody] ClaimRequest request)
        {
            var status = _service.AssignServer(request.AffinityKey, request.InstanceId);
            if (status is null)
                return StatusCode(503, new { message = "No capacity available." });

            return Ok(ServerStatusResponse.From(status));
        }

        [HttpPost("release")]
        public IActionResult Release([FromBody] ReleaseRequest request)
        {
            if (_service.ReleaseServer(request.ServerId, request.PlayerJoined))
                return NoContent();

            return NotFound(new { message = "Server not registered." });
        }

        [HttpGet("servers")]
        public ActionResult<IEnumerable<ServerStatusResponse>> Servers()
        {
            var servers = _service.GetServers().Select(ServerStatusResponse.From);
            return Ok(servers);
        }

        public record RegisterRequest
        {
            public Guid? ServerId { get; init; }
            public string? IpAddress { get; init; }
            public int Port { get; init; }
            public string? Region { get; init; }
            public int MaxPlayers { get; init; } = 100;
            public int CurrentLoad { get; init; }
            public List<Guid>? Instances { get; init; }
        }

        public record HeartbeatRequest
        {
            public Guid ServerId { get; init; }
            public int CurrentLoad { get; init; }
            public int? MaxPlayers { get; init; }
            public List<Guid>? Instances { get; init; }
        }

        public record ClaimRequest
        {
            public Guid? InstanceId { get; init; }
            public string? AffinityKey { get; init; }
        }

        public record ReleaseRequest
        {
            public Guid ServerId { get; init; }
            public bool PlayerJoined { get; init; }
        }

        public record ServerStatusResponse
        {
            public Guid ServerId { get; init; }
            public string? IpAddress { get; init; }
            public int Port { get; init; }
            public string? Region { get; init; }
            public int CurrentLoad { get; init; }
            public int PendingConnections { get; init; }
            public int MaxPlayers { get; init; }
            public bool IsAcceptingPlayers { get; init; }
            public DateTime LastHeartbeatUtc { get; init; }
            public IReadOnlyCollection<Guid> Instances { get; init; } = Array.Empty<Guid>();

            public static ServerStatusResponse From(LoadBalancerService.ServerStatus status)
                => new()
                {
                    ServerId = status.Id,
                    IpAddress = status.Ip,
                    Port = status.Port,
                    Region = status.Region,
                    CurrentLoad = status.CurrentLoad,
                    PendingConnections = status.PendingConnections,
                    MaxPlayers = status.MaxPlayers,
                    IsAcceptingPlayers = status.IsAcceptingPlayers,
                    LastHeartbeatUtc = status.LastHeartbeatUtc,
                    Instances = status.Instances
                };
        }
    }
}
