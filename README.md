# Snack Counter API

This .NET API automates snack refills for Oslo (NOK) and Bengaluru (INR). An empty-item request enters a shared office queue. A background worker offers it to one eligible volunteer at a time, considering prior workload, office presence, leave, and earlier offers. The volunteer accepts from the correct office, restocks the item, and records the purchase. A transaction updates inventory and the office's monthly budget together; spending cannot exceed that budget.

Purchase-linked reimbursement claims are submitted through a durable outbox to the separate Mock HR service. HR decisions return through a signed, deduplicated webhook. Durable chat notifications are delivered live with SignalR and remain available through the API inbox. Reports keep NOK and INR totals separate, with an optional configured demo conversion for a combined view.

The core design and code references are in [DESIGN.md](DESIGN.md). Run the automated suite with:

```powershell
dotnet test API.SnackCounter.slnx
```

For local development, configure `ConnectionStrings:SnackCounter` for SQL Server Express. Start Mock HR and the API in separate terminals:

```powershell
dotnet run --project MockHr.Service/MockHr.Service.csproj --urls http://localhost:5205
dotnet run --project API.SnackCounter.csproj --urls http://localhost:5121
```

Angular/UI integration is optional and remains a work in progress; the backend exposes REST endpoints and SignalR notifications for a client.
