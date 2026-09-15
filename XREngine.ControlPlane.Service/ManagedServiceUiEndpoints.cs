using Microsoft.AspNetCore.Http.HttpResults;

namespace XREngine.ControlPlane.Service;

/// <summary>Minimal same-origin sample UI for the loopback managed-instance service.</summary>
public static class ManagedServiceUiEndpoints
{
    /// <summary>Maps the authenticated sample UI and its credential-safe native launch action.</summary>
    public static void MapManagedServiceUi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/", () => Results.Content(Page, "text/html; charset=utf-8"));
        app.MapPost("/ui/v1/instances/{id}/reservations/{reservationId}/launch", LaunchNativeClient);
    }

    private static IResult LaunchNativeClient(
        string id,
        string reservationId,
        HttpContext context,
        InMemoryControlPlane registry,
        LocalWorkerSupervisor supervisor,
        LocalNativeGameLauncher launcher)
    {
        if (context.Items[typeof(LocalApiUser)] is not LocalApiUser user)
            return TypedResults.Unauthorized();
        
        MultiplayerInstanceInfo? instance = registry.GetInstance(id).Value;
        if (instance is null || !string.Equals(instance.TenantId, user.TenantId, StringComparison.Ordinal))
            return TypedResults.NotFound();
        
        if (!launcher.IsEnabled)
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "Native launcher unavailable", detail: "The local service has not enabled an allowlisted native game executable.");

        ControlPlaneResult<ManagedAdmissionReservation> reservation = registry.GetReservation(id, reservationId);
        if (!reservation.Success || reservation.Value is null || (!user.IsAdministrator && reservation.Value.AccountId != user.UserId))
            return TypedResults.NotFound();
        
        ControlPlaneResult<ReserveManagedAdmissionResult> delivered = registry.GetDeliveredAdmission(id, reservationId);
        if (!delivered.Success || delivered.Value?.Grant is not { } grant)
            return Results.Conflict(new { code = delivered.FailureReason.ToString(), message = delivered.Message });
        
        ManagedWorkerLaunch? worker = supervisor.GetLaunch(id);
        if (worker is null)
            return TypedResults.NotFound();

        int processId = launcher.Launch(new ManagedClientLaunch
        {
            Handoff = new()
            {
                SessionId = worker.SessionId,
                ClientId = grant.ClientId,
                AccountId = grant.AccountId,
                ReservationId = grant.ReservationId,
                AdmissionSecret = grant.Secret,
                WorkerGeneration = grant.Generation,
                ResumeRequested = grant.Purpose == ManagedAdmissionGrantPurpose.Resume,
                CredentialEpoch = grant.CredentialEpoch,
                Endpoint = worker.AdvertisedEndpoint,
                WorldAsset = worker.WorldPackage.Asset,
            },
            WorldPackage = worker.WorldPackage,
            PackageRootPath = worker.PackageRootPath,
            CacheRootPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XREngine", "ManagedWorldCache"),
            WorldEntryPoint = worker.WorldEntryPoint,
            GameBootstrapId = worker.GameBootstrapId,
            BuildVersion = worker.BuildVersion,
        });
        return Results.Accepted(value: new { processId, status = "starting" });
    }

    private const string Page = """
<!doctype html><html lang="en"><head><meta charset="utf-8"><title>XREngine local instances</title>
<style>body{font:14px system-ui;margin:2rem;max-width:1100px}input,button,select{margin:.2rem;padding:.35rem}table{border-collapse:collapse;width:100%;margin-top:1rem}th,td{border:1px solid #bbb;padding:.45rem;text-align:left}pre{white-space:pre-wrap;background:#f5f5f5;padding:1rem}.hidden{display:none}</style></head>
<body><h1>XREngine local managed instances</h1><p>This loopback sample keeps its bearer credential only in this page's memory. Native launch transfers the delivered player credential directly to an allowlisted local game process; it is never placed in a URL.</p>
<label>Bearer <input id="token" type="password" autocomplete="off" size="52"></label><button id="refresh">Refresh</button>
<fieldset><legend>Create instance</legend><label>Package <input id="package" required></label><label>Name <input id="name" value="Local instance"></label><label>Players <input id="players" type="number" min="1" value="4"></label><label>Public <input id="public" type="checkbox" checked></label><button id="create">Create</button></fieldset>
<fieldset><legend>Reservation</legend><label>Instance <input id="reservationInstance"></label><label>Reservation <input id="reservation"></label><button id="leave">Leave</button><button id="kick">Kick</button></fieldset>
<pre id="status">Enter a bearer credential, then Refresh.</pre><table><thead><tr><th>Name</th><th>World</th><th>Lifecycle</th><th>Occupancy</th><th>Actions</th></tr></thead><tbody id="instances"></tbody></table>
<script>
const status=document.querySelector('#status'), body=document.querySelector('#instances');
function op(){return crypto.randomUUID()} function headers(){return {'Authorization':'Bearer '+document.querySelector('#token').value,'Content-Type':'application/json'}}
async function api(url, options={}){let r=await fetch(url,{...options,headers:{...headers(),...(options.headers||{})}});let p=await r.json().catch(()=>({}));if(!r.ok)throw new Error(p.message||p.detail||p.code||r.status);return p}
function show(x){status.textContent=typeof x==='string'?x:JSON.stringify(x,null,2)}
async function refresh(){try{let d=await api('/v1/instances');body.replaceChildren(...d.items.map(row));show('Directory updated.');}catch(e){show(e.message)}}
function row(i){let tr=document.createElement('tr'), room=document.createElement('td'), acts=document.createElement('td');tr.innerHTML=`<td>${escape(i.displayName||i.instanceId)}</td><td>${escape(i.world.worldId)}@${escape(i.world.revisionId)}</td><td>${escape(i.state)}</td><td>${i.synchronizedPlayers}/${i.maxPlayers} synchronized; ${i.connectedPlayers} connected</td>`;for(let [name,fn] of [['Reserve',()=>reserve(i)],['Drain',()=>action(i,'drain')],['Stop',()=>action(i,'stop')]]){let b=document.createElement('button');b.textContent=name;b.onclick=fn;acts.append(b)}tr.append(acts);return tr}
function escape(v){return String(v).replace(/[&<>\"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','\"':'&quot;'}[c]))}
async function action(i,what){try{show(await api(`/v1/instances/${encodeURIComponent(i.instanceId)}/${what}`,{method:'POST'}));refresh()}catch(e){show(e.message)}}
async function reserve(i){try{let r=await api(`/v1/instances/${encodeURIComponent(i.instanceId)}/reservations`,{method:'POST',body:JSON.stringify({operationId:op(),clientId:'web-'+op(),buildVersion:i.world.requiredBuildVersion||'dev'})});document.querySelector('#reservationInstance').value=i.instanceId;document.querySelector('#reservation').value=r.reservationId;show('Reservation requested. Waiting for installed player grant…');await waitHandoff(i.instanceId,r.reservationId)}catch(e){show(e.message)}}
async function waitHandoff(id,res){for(let n=0;n<40;n++){await new Promise(r=>setTimeout(r,500));try{let p=await api(`/ui/v1/instances/${encodeURIComponent(id)}/reservations/${encodeURIComponent(res)}/launch`,{method:'POST'});show('Native client process started: '+p.processId);return}catch(e){if(n===39)throw e}}}
document.querySelector('#refresh').onclick=refresh;document.querySelector('#create').onclick=async()=>{try{let p=await api('/v1/instances',{method:'POST',body:JSON.stringify({operationId:op(),packageId:document.querySelector('#package').value,displayName:document.querySelector('#name').value,maxPlayers:+document.querySelector('#players').value,isPublic:document.querySelector('#public').checked})});show(p);refresh()}catch(e){show(e.message)}};
document.querySelector('#leave').onclick=async()=>{try{let id=document.querySelector('#reservationInstance').value,res=document.querySelector('#reservation').value;await api(`/v1/instances/${encodeURIComponent(id)}/reservations/${encodeURIComponent(res)}`,{method:'DELETE'});show('Reservation cancelled.');refresh()}catch(e){show(e.message)}};document.querySelector('#kick').onclick=async()=>{try{let id=document.querySelector('#reservationInstance').value,res=document.querySelector('#reservation').value;await api(`/v1/instances/${encodeURIComponent(id)}/reservations/${encodeURIComponent(res)}/kick`,{method:'POST'});show('Kick requested.');refresh()}catch(e){show(e.message)}};
</script></body></html>
""";
}
