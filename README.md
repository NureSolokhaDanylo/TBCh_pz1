# Launching 3 Nodes

### Option 1: Native Windows / .NET (Interactive CLI in 3 separate windows)
Requires [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
powershell -ExecutionPolicy Bypass -File .\start_3_nodes.ps1
```
*(Optional: add `-Reset` to wipe old blocks and start cleanly from Genesis).*

---

### Option 2: Docker / Containerized (Zero .NET dependency)
Does **not** require .NET installed. Requires [Docker Desktop](https://www.docker.com/).

```bash
docker compose up --build
```

---

### Web Dashboards & APIs
Once started, the nodes are accessible at:
- **Node 1**: http://localhost:8081/
- **Node 2**: http://localhost:8082/
- **Node 3**: http://localhost:8083/
