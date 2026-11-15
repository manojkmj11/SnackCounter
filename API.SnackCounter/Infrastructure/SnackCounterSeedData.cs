using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SnackCounter.Domain;

namespace SnackCounter.Infrastructure;

public static class SnackCounterSeedData
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<SnackCounterDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow();

        var employees = await EnsureEmployeesAsync(database);
        await EnsureEmployeeOfficesAndRolesAsync(database);

        var currentDay = DateOnly.FromDateTime(now.UtcDateTime);
        var previousMonth = new DateOnly(currentDay.Year, currentDay.Month, 1).AddMonths(-1);
        var monthEnd = previousMonth.AddMonths(1).AddDays(-1);
        var items = await EnsureSnackItemsAsync(database);

        var alreadySeeded = await database.MonthlyBudgets.AnyAsync(budget =>
            budget.Year == previousMonth.Year && budget.Month == previousMonth.Month);
        if (!alreadySeeded)
        {
            await SeedPreviousMonthAsync(database, employees, items, previousMonth, monthEnd);
        }

        await EnsureCurrentOpenRefillsAsync(database, items, now);
    }

    private static async Task<Dictionary<string, Volunteer>> EnsureEmployeesAsync(SnackCounterDbContext database)
    {
        var demoEmployees = new[]
        {
            new Volunteer("oslo-01", OfficeLocation.Oslo, Currency.NOK) { DisplayName = "Anika Rao", Email = "anika.rao@snackcounter.local" },
            new Volunteer("oslo-02", OfficeLocation.Oslo, Currency.NOK) { DisplayName = "Erik Nilsen", Email = "erik.nilsen@snackcounter.local" },
            new Volunteer("blr-01", OfficeLocation.Bengaluru, Currency.INR) { DisplayName = "Meera Iyer", Email = "meera.iyer@snackcounter.local" },
            new Volunteer("blr-02", OfficeLocation.Bengaluru, Currency.INR) { DisplayName = "Kiran Shah", Email = "kiran.shah@snackcounter.local" },
            new Volunteer("hr-oslo", OfficeLocation.Oslo, Currency.NOK) { DisplayName = "Nora Berg", Email = "nora.berg@snackcounter.local" },
            new Volunteer("hr-blr", OfficeLocation.Bengaluru, Currency.INR) { DisplayName = "Ravi Menon", Email = "ravi.menon@snackcounter.local" },
            new Volunteer("manager-01", OfficeLocation.Oslo, Currency.NOK) { DisplayName = "Sofia Lind", Email = "sofia.lind@snackcounter.local" }
        };

        var existingEmployees = await database.Volunteers.ToDictionaryAsync(employee => employee.EmployeeId);
        foreach (var demoEmployee in demoEmployees)
        {
            if (!existingEmployees.TryGetValue(demoEmployee.EmployeeId, out var existing))
            {
                database.Volunteers.Add(demoEmployee);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(existing.DisplayName) || existing.DisplayName == existing.EmployeeId)
                {
                    existing.DisplayName = demoEmployee.DisplayName;
                }

                if (string.IsNullOrWhiteSpace(existing.Email))
                {
                    existing.Email = demoEmployee.Email;
                }
            }
        }
        await database.SaveChangesAsync();
        return await database.Volunteers.ToDictionaryAsync(employee => employee.EmployeeId);
    }

    private static async Task EnsureEmployeeOfficesAndRolesAsync(SnackCounterDbContext database)
    {
        var memberships = new (string EmployeeId, OfficeLocation Office)[]
        {
            ("oslo-01", OfficeLocation.Oslo),
            ("oslo-02", OfficeLocation.Oslo),
            ("blr-01", OfficeLocation.Oslo),
            ("blr-01", OfficeLocation.Bengaluru),
            ("blr-02", OfficeLocation.Bengaluru),
            ("oslo-02", OfficeLocation.Bengaluru)
        };
        foreach (var membership in memberships)
        {
            var exists = await database.EmployeeOffices.AnyAsync(entry =>
                entry.EmployeeId == membership.EmployeeId && entry.Office == membership.Office);
            if (!exists)
            {
                database.EmployeeOffices.Add(new OfficeVolunteerEligibility
                {
                    EmployeeId = membership.EmployeeId,
                    Office = membership.Office
                });
            }
        }

        var roleAssignments = new (string EmployeeId, string RoleName)[]
        {
            ("oslo-01", "Employee"),
            ("oslo-02", "Employee"),
            ("blr-01", "Employee"),
            ("blr-02", "Employee"),
            ("hr-oslo", "Employee"),
            ("hr-oslo", "HR"),
            ("hr-blr", "Employee"),
            ("hr-blr", "HR"),
            ("manager-01", "Employee"),
            ("manager-01", "Manager")
        };
        foreach (var assignment in roleAssignments)
        {
            var exists = await database.EmployeeRoles.AnyAsync(role =>
                role.EmployeeId == assignment.EmployeeId && role.RoleName == assignment.RoleName);
            if (!exists)
            {
                database.EmployeeRoles.Add(new EmployeeRole
                {
                    EmployeeId = assignment.EmployeeId,
                    RoleName = assignment.RoleName
                });
            }
        }

        await database.SaveChangesAsync();
    }

    private static async Task<Dictionary<(OfficeLocation Office, string Name), SnackItem>> EnsureSnackItemsAsync(
        SnackCounterDbContext database)
    {
        var definitions = new (OfficeLocation Office, string Name, string Unit, decimal Threshold)[]
        {
            (OfficeLocation.Oslo, "Coffee", "bag", 2m),
            (OfficeLocation.Oslo, "Fruit boxes", "box", 4m),
            (OfficeLocation.Oslo, "Granola bars", "box", 3m),
            (OfficeLocation.Bengaluru, "Tea", "pack", 3m),
            (OfficeLocation.Bengaluru, "Biscuits", "box", 4m),
            (OfficeLocation.Bengaluru, "Peanuts", "pack", 3m)
        };

        var items = await database.SnackItems.ToDictionaryAsync(item => (item.Office, item.Name));
        foreach (var definition in definitions)
        {
            if (items.ContainsKey((definition.Office, definition.Name)))
            {
                continue;
            }

            var item = new SnackItem
            {
                Id = StableId($"item:{definition.Office}:{definition.Name}"),
                Office = definition.Office,
                Name = definition.Name,
                Unit = definition.Unit,
                ReorderThreshold = definition.Threshold,
                QuantityOnHand = 0m,
                IsActive = true
            };
            database.SnackItems.Add(item);
            items.Add((item.Office, item.Name), item);
        }

        await database.SaveChangesAsync();
        return items;
    }

    private static async Task SeedPreviousMonthAsync(
        SnackCounterDbContext database,
        IReadOnlyDictionary<string, Volunteer> employees,
        IReadOnlyDictionary<(OfficeLocation Office, string Name), SnackItem> items,
        DateOnly month,
        DateOnly monthEnd)
    {
        var purchases = new[]
        {
            new HistoricalPurchase(OfficeLocation.Oslo, "Coffee", "bag", 8m, 14m, 22m, 300m, "oslo-01", 5, ReimbursementClaimStatus.Reimbursed),
            new HistoricalPurchase(OfficeLocation.Oslo, "Fruit boxes", "box", 5m, 12m, 15m, 75m, "oslo-02", 18, ReimbursementClaimStatus.Denied),
            new HistoricalPurchase(OfficeLocation.Oslo, "Granola bars", "box", 6m, 8m, 7m, 112.5m, "oslo-01", 26, ReimbursementClaimStatus.Reimbursed),
            new HistoricalPurchase(OfficeLocation.Bengaluru, "Tea", "pack", 4m, 12m, 16m, 300m, "blr-01", 4, ReimbursementClaimStatus.Reimbursed),
            new HistoricalPurchase(OfficeLocation.Bengaluru, "Biscuits", "box", 6m, 20m, 24m, 300m, "blr-02", 16, ReimbursementClaimStatus.Approved),
            new HistoricalPurchase(OfficeLocation.Bengaluru, "Peanuts", "pack", 8m, 12m, 15m, 150m, "blr-01", 25, ReimbursementClaimStatus.Submitted)
        };

        var refills = new Dictionary<string, Refill>();
        var claimsByRefill = new Dictionary<Guid, ReimbursementClaim>();
        foreach (var purchase in purchases)
        {
            var item = items[(purchase.Office, purchase.Item)];
            var purchasedAt = At(month, purchase.PurchaseDay, 10);
            var refillId = StableId($"refill:{month:yyyy-MM}:{purchase.Office}:{purchase.Item}");
            var claimId = StableId($"claim:{month:yyyy-MM}:{purchase.Office}:{purchase.Item}");
            var refillStatus = purchase.ClaimStatus switch
            {
                ReimbursementClaimStatus.Submitted => RefillStatus.ClaimSubmitted,
                ReimbursementClaimStatus.Approved => RefillStatus.Approved,
                ReimbursementClaimStatus.Denied => RefillStatus.ClaimRejected,
                _ => RefillStatus.Reimbursed
            };

            var refill = new Refill(refillId, purchase.Office, purchase.Item, purchasedAt.AddDays(-1))
            {
                AssignedVolunteer = employees[purchase.EmployeeId],
                SnackItemId = item.Id,
                Status = refillStatus,
                PurchaseAmount = purchase.RestockQuantity * purchase.UnitCost,
                PurchaseCurrency = OfficeFinance.BudgetCurrency(purchase.Office),
                PurchasedAt = purchasedAt,
                PurchaseDescription = $"October demo restock: {purchase.RestockQuantity} {purchase.Unit}",
                HrClaimId = claimId.ToString(),
                Version = 3
            };
            refills.Add(purchase.Item, refill);
            database.Refills.Add(refill);

            var openingAt = At(month, 1, 8);
            var consumedAt = AtDate(monthEnd, 18, 0);
            database.StockMovements.AddRange(
                new StockMovement
                {
                    Id = StableId($"movement:{refillId}:opening"),
                    SnackItem = item,
                    SnackItemId = item.Id,
                    Kind = StockMovementKind.Adjustment,
                    Quantity = purchase.OpeningQuantity,
                    OccurredAt = openingAt,
                    Note = "Demo opening stock for previous month"
                },
                new StockMovement
                {
                    Id = StableId($"movement:{refillId}:restock"),
                    SnackItem = item,
                    SnackItemId = item.Id,
                    Kind = StockMovementKind.Restock,
                    Quantity = purchase.RestockQuantity,
                    OccurredAt = purchasedAt,
                    ActorEmployeeId = purchase.EmployeeId,
                    RefillId = refillId,
                    UnitCost = purchase.UnitCost,
                    CostCurrency = OfficeFinance.BudgetCurrency(purchase.Office),
                    Note = "Demo purchase"
                },
                new StockMovement
                {
                    Id = StableId($"movement:{refillId}:consumption"),
                    SnackItem = item,
                    SnackItemId = item.Id,
                    Kind = StockMovementKind.Consumption,
                    Quantity = -purchase.ConsumedQuantity,
                    OccurredAt = consumedAt,
                    Note = "Demo previous-month consumption"
                });
            item.QuantityOnHand = purchase.OpeningQuantity + purchase.RestockQuantity - purchase.ConsumedQuantity;

            var attemptAt = purchasedAt.AddHours(-2);
            database.RefillAssignmentAttempts.Add(new RefillAssignmentAttempt
            {
                Id = StableId($"attempt:{refillId}"),
                RefillId = refillId,
                EmployeeId = purchase.EmployeeId,
                LocalDate = DateOnly.FromDateTime(attemptAt.UtcDateTime),
                AttemptNumber = 1,
                Outcome = RefillAttemptOutcome.Accepted,
                WorkingMode = WorkingMode.WFO,
                WorkingOffice = purchase.Office,
                AttemptedAt = attemptAt
            });

            var claimSubmittedAt = purchasedAt.AddDays(1);
            var claimDecidedAt = purchasedAt.AddDays(3);
            var isPending = purchase.ClaimStatus == ReimbursementClaimStatus.Submitted;
            var claim = new ReimbursementClaim
            {
                Id = claimId,
                EmployeeId = purchase.EmployeeId,
                Office = purchase.Office,
                RefillId = refillId,
                IdempotencyKey = $"demo-{claimId:N}",
                Amount = purchase.RestockQuantity * purchase.UnitCost,
                Currency = OfficeFinance.BudgetCurrency(purchase.Office),
                Description = refill.PurchaseDescription!,
                Status = purchase.ClaimStatus,
                ProcessingByEmployeeId = isPending ? null : purchase.Office == OfficeLocation.Oslo ? "hr-oslo" : "hr-blr",
                ProcessingAt = isPending ? null : claimSubmittedAt.AddHours(2),
                ProcessingLeaseExpiresAt = null,
                DecisionReason = purchase.ClaimStatus switch
                {
                    ReimbursementClaimStatus.Denied => "Demo claim denied: receipt details need correction.",
                    ReimbursementClaimStatus.Approved => "Demo claim approved.",
                    _ => null
                },
                SubmittedAt = claimSubmittedAt,
                DecidedAt = isPending ? null : claimDecidedAt,
                ReimbursedAt = purchase.ClaimStatus == ReimbursementClaimStatus.Reimbursed ? claimDecidedAt.AddDays(1) : null,
                PayrollAmount = employees[purchase.EmployeeId].PayrollCurrency == OfficeFinance.BudgetCurrency(purchase.Office)
                    ? purchase.RestockQuantity * purchase.UnitCost
                    : null,
                PayrollCurrency = employees[purchase.EmployeeId].PayrollCurrency,
                ExchangeRate = employees[purchase.EmployeeId].PayrollCurrency == OfficeFinance.BudgetCurrency(purchase.Office) ? 1m : null,
                ExchangeRateAt = employees[purchase.EmployeeId].PayrollCurrency == OfficeFinance.BudgetCurrency(purchase.Office) ? purchasedAt : null,
                ExchangeRateSource = employees[purchase.EmployeeId].PayrollCurrency == OfficeFinance.BudgetCurrency(purchase.Office) ? "Same currency" : null
            };
            database.ReimbursementClaims.Add(claim);
            claimsByRefill.Add(refillId, claim);

            database.AuditRecords.Add(new AuditRecord
            {
                ActorId = purchase.EmployeeId,
                Action = "ClaimSubmitted",
                EntityType = nameof(ReimbursementClaim),
                EntityId = claimId.ToString(),
                ChangeSummaryJson = JsonSerializer.Serialize(new { claim.Status, claim.Amount, claim.Currency }),
                OccurredAt = claimSubmittedAt
            });
            if (!isPending)
            {
                database.AuditRecords.Add(new AuditRecord
                {
                    ActorId = claim.ProcessingByEmployeeId!,
                    Action = $"Claim{purchase.ClaimStatus}",
                    EntityType = nameof(ReimbursementClaim),
                    EntityId = claimId.ToString(),
                    ChangeSummaryJson = JsonSerializer.Serialize(new { claim.Status, claim.DecisionReason }),
                    OccurredAt = claimDecidedAt
                });
            }
        }

        database.EmployeeLeaves.AddRange(
            new EmployeeLeave
            {
                Id = StableId($"leave:{month:yyyy-MM}:oslo-02"),
                EmployeeId = "oslo-02",
                Office = OfficeLocation.Oslo,
                StartDate = month.AddDays(11),
                EndDate = month.AddDays(12),
                Status = EmployeeLeaveStatus.Confirmed,
                SubmittedAt = At(month, 3, 9)
            },
            new EmployeeLeave
            {
                Id = StableId($"leave:{month:yyyy-MM}:blr-02"),
                EmployeeId = "blr-02",
                Office = OfficeLocation.Bengaluru,
                StartDate = month.AddDays(19),
                EndDate = month.AddDays(21),
                Status = EmployeeLeaveStatus.Confirmed,
                SubmittedAt = At(month, 5, 9)
            });

        foreach (var office in new[] { OfficeLocation.Oslo, OfficeLocation.Bengaluru })
        {
            var spent = purchases.Where(purchase => purchase.Office == office)
                .Sum(purchase => purchase.RestockQuantity * purchase.UnitCost);
            var budget = OfficeFinance.MonthlyBudget(office);
            database.MonthlyBudgets.Add(new MonthlyBudget
            {
                Office = office,
                Year = month.Year,
                Month = month.Month,
                Currency = OfficeFinance.BudgetCurrency(office),
                BudgetAmount = budget,
                SpentAmount = spent,
                WarningThresholdPercent = 80m,
                SnapshotAt = AtDate(monthEnd, 23, 59)
            });

            var latestPurchase = purchases.Last(purchase => purchase.Office == office);
            database.RefillRotations.Add(new RefillRotation
            {
                Office = office,
                LastEmployeeId = latestPurchase.EmployeeId,
                LastAttemptedLocalDate = monthEnd,
                LastSelectedAt = AtDate(monthEnd, 16, 0)
            });
        }

        var osloTop = purchases.Where(purchase => purchase.Office == OfficeLocation.Oslo)
            .OrderByDescending(purchase => purchase.ConsumedQuantity).First();
        var bengaluruTop = purchases.Where(purchase => purchase.Office == OfficeLocation.Bengaluru)
            .OrderByDescending(purchase => purchase.ConsumedQuantity).First();
        var osloSpent = purchases.Where(purchase => purchase.Office == OfficeLocation.Oslo)
            .Sum(purchase => purchase.RestockQuantity * purchase.UnitCost);
        var bengaluruSpent = purchases.Where(purchase => purchase.Office == OfficeLocation.Bengaluru)
            .Sum(purchase => purchase.RestockQuantity * purchase.UnitCost);
        var monthLabel = month.ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
        var osloBudget = OfficeFinance.MonthlyBudget(OfficeLocation.Oslo);
        var bengaluruBudget = OfficeFinance.MonthlyBudget(OfficeLocation.Bengaluru);
        var monthEndAt = AtDate(monthEnd, 23, 59);

        var conversations = await EnsureConversationsAsync(database, employees.Keys);
        var messages = new List<ConversationMessage>();
        var notifications = new List<Notification>();
        var officePayloads = new Dictionary<OfficeLocation, object>
        {
            [OfficeLocation.Oslo] = new
            {
                period = monthLabel,
                office = "Oslo",
                currency = "NOK",
                spent = osloSpent,
                budget = osloBudget,
                remaining = osloBudget - osloSpent,
                topConsumedItem = osloTop.Item,
                topConsumedQuantity = osloTop.ConsumedQuantity,
                budgetStatus = "Met"
            },
            [OfficeLocation.Bengaluru] = new
            {
                period = monthLabel,
                office = "Bengaluru",
                currency = "INR",
                spent = bengaluruSpent,
                budget = bengaluruBudget,
                remaining = bengaluruBudget - bengaluruSpent,
                topConsumedItem = bengaluruTop.Item,
                topConsumedQuantity = bengaluruTop.ConsumedQuantity,
                budgetStatus = "UnusedBudget"
            }
        };

        foreach (var office in new[] { OfficeLocation.Oslo, OfficeLocation.Bengaluru })
        {
            var officePayload = JsonSerializer.Serialize(officePayloads[office]);
            var officeName = office.ToString();
            var officeConversation = conversations[StableId($"conversation:office:{office}")];
            var topItem = office == OfficeLocation.Oslo ? osloTop : bengaluruTop;
            var spent = office == OfficeLocation.Oslo ? osloSpent : bengaluruSpent;
            var budgetAmount = OfficeFinance.MonthlyBudget(office);
            var currency = OfficeFinance.BudgetCurrency(office);
            AddOfficeEvent(officeConversation, "MonthlyConsumptionRanked",
                $"{monthLabel} top consumed item at {officeName}: {topItem.Item} ({topItem.ConsumedQuantity} {topItem.Unit}).",
                officePayload, office, monthEndAt, messages, notifications);
            AddOfficeEvent(officeConversation,
                office == OfficeLocation.Oslo ? "BudgetMetAtClose" : "BudgetUnusedAtClose",
                office == OfficeLocation.Oslo
                    ? $"{monthLabel} {officeName} budget met: {spent:N0} {currency} of {budgetAmount:N0} {currency}."
                    : $"{monthLabel} {officeName} used {spent:N0} {currency} of {budgetAmount:N0} {currency}; {budgetAmount - spent:N0} {currency} remained.",
                officePayload, office, monthEndAt, messages, notifications);
            var emptyItem = office == OfficeLocation.Oslo ? "Coffee" : "Tea";
            AddOfficeEvent(officeConversation, "StockDepleted",
                $"{emptyItem} stock was empty at {officeName} at month close; remaining budget was {budgetAmount - spent:N0} {currency}.",
                officePayload, office, monthEndAt, messages, notifications);
        }

        foreach (var purchase in purchases)
        {
            var refill = refills[purchase.Item];
            var claim = claimsByRefill[refill.Id];
            var employeeConversation = conversations[StableId($"conversation:employee:{purchase.EmployeeId}")];
            var amountText = $"{claim.Amount:N2} {claim.Currency}";
            var text = claim.Status switch
            {
                ReimbursementClaimStatus.Submitted => $"Your reimbursement claim for {amountText} was sent to HR and is awaiting review.",
                ReimbursementClaimStatus.Approved => $"Your reimbursement claim for {amountText} was approved.",
                ReimbursementClaimStatus.Denied => $"Your reimbursement claim for {amountText} was denied. {claim.DecisionReason}",
                ReimbursementClaimStatus.Reimbursed => $"Your reimbursement claim for {amountText} was reimbursed.",
                _ => $"Your reimbursement claim for {amountText} was updated."
            };
            var decisionAt = claim.DecidedAt ?? claim.SubmittedAt;
            AddEmployeeEvent(employeeConversation, claim.EmployeeId,
                claim.Status == ReimbursementClaimStatus.Submitted ? "ClaimSubmitted" : "ClaimDecided",
                text,
                JsonSerializer.Serialize(new { claimId = claim.Id, claim.Status, claim.Amount, claim.Currency }),
                decisionAt,
                claim.Status != ReimbursementClaimStatus.Submitted,
                messages,
                notifications);

            var hrConversation = conversations[StableId("conversation:hr")];
            var hrPayload = JsonSerializer.Serialize(new
            {
                claimId = claim.Id,
                employeeId = claim.EmployeeId,
                office = claim.Office.ToString(),
                claim.Amount,
                currency = claim.Currency.ToString(),
                status = claim.Status.ToString()
            });
            AddHrEvent(hrConversation, "ClaimSubmitted",
                $"{claim.EmployeeId} submitted a {claim.Amount:N2} {claim.Currency} reimbursement claim for {claim.Office} ({claim.Status}).",
                hrPayload,
                claim.SubmittedAt,
                claim.Status != ReimbursementClaimStatus.Submitted,
                messages,
                notifications);
        }

        var combinedPayload = JsonSerializer.Serialize(new
        {
            period = monthLabel,
            offices = new[]
            {
                new { office = "Oslo", currency = "NOK", spent = osloSpent, budget = osloBudget, remaining = osloBudget - osloSpent },
                new { office = "Bengaluru", currency = "INR", spent = bengaluruSpent, budget = bengaluruBudget, remaining = bengaluruBudget - bengaluruSpent }
            },
            totalsByCurrency = new { NOK = osloSpent, INR = bengaluruSpent },
            reportingCurrency = "NOK",
            totalInReportingCurrency = decimal.Round(osloSpent + bengaluruSpent * 0.1m, 2, MidpointRounding.ToEven),
            exchangeRatePolicy = "Configured static demonstration FX table; INR to NOK rate 0.1; converted per purchase and rounded to 2 decimals."
        });
        foreach (var (office, payload, top) in new[]
        {
            (OfficeLocation.Oslo, officePayloads[OfficeLocation.Oslo], osloTop),
            (OfficeLocation.Bengaluru, officePayloads[OfficeLocation.Bengaluru], bengaluruTop)
        })
        {
            database.MonthlyReports.Add(new MonthlyReport
            {
                Id = StableId($"report:office:{month:yyyy-MM}:{office}"),
                Kind = MonthlyReportKind.Office,
                Office = office,
                Year = month.Year,
                Month = month.Month,
                AsOf = monthEndAt,
                ReportingCurrency = null,
                PayloadJson = JsonSerializer.Serialize(new { data = payload, topConsumedItem = top.Item }),
                GeneratedAt = monthEndAt
            });
        }
        database.MonthlyReports.Add(new MonthlyReport
        {
            Id = StableId($"report:combined:{month:yyyy-MM}"),
            Kind = MonthlyReportKind.Combined,
            Office = null,
            Year = month.Year,
            Month = month.Month,
            AsOf = monthEndAt,
            ReportingCurrency = Currency.NOK,
            PayloadJson = combinedPayload,
            GeneratedAt = monthEndAt
        });

        var managerConversation = conversations[StableId("conversation:employee:manager-01")];
        AddEmployeeEvent(managerConversation, "manager-01", "MonthlyReportReady",
            $"The combined {monthLabel} report is available with original NOK/INR subtotals and a converted NOK total.",
            combinedPayload, monthEndAt, false, messages, notifications);

        foreach (var notification in notifications)
        {
            var eventId = StableId($"outbox:{notification.DeduplicationKey}");
            database.OutboxMessages.Add(new OutboxMessage
            {
                Id = StableId($"outbox-message:{notification.DeduplicationKey}"),
                EventId = eventId,
                EventType = notification.EventType,
                PayloadJson = notification.PayloadJson ?? "{}",
                OccurredAt = notification.CreatedAt,
                AvailableAt = notification.CreatedAt,
                ProcessedAt = notification.DeliveredAt,
                AttemptCount = 1
            });
        }

        database.ConversationMessages.AddRange(messages);
        await database.Notifications.AddRangeAsync(notifications);
        await database.SaveChangesAsync();
    }

    private static async Task<Dictionary<Guid, Conversation>> EnsureConversationsAsync(
        SnackCounterDbContext database,
        IEnumerable<string> employeeIds)
    {
        var conversations = await database.Conversations.ToDictionaryAsync(conversation => conversation.Id);
        foreach (var employeeId in employeeIds)
        {
            var id = StableId($"conversation:employee:{employeeId}");
            if (!conversations.ContainsKey(id))
            {
                var conversation = new Conversation
                {
                    Id = id,
                    Kind = ConversationKind.Employee,
                    EmployeeId = employeeId,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                conversations.Add(id, conversation);
                database.Conversations.Add(conversation);
            }
        }

        foreach (var office in new[] { OfficeLocation.Oslo, OfficeLocation.Bengaluru })
        {
            var id = StableId($"conversation:office:{office}");
            if (!conversations.ContainsKey(id))
            {
                var conversation = new Conversation
                {
                    Id = id,
                    Kind = ConversationKind.Office,
                    Office = office,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                conversations.Add(id, conversation);
                database.Conversations.Add(conversation);
            }
        }

        var hrId = StableId("conversation:hr");
        if (!conversations.ContainsKey(hrId))
        {
            var conversation = new Conversation
            {
                Id = hrId,
                Kind = ConversationKind.Hr,
                CreatedAt = DateTimeOffset.UtcNow
            };
            conversations.Add(hrId, conversation);
            database.Conversations.Add(conversation);
        }

        await database.SaveChangesAsync();
        return conversations;
    }

    private static void AddOfficeEvent(
        Conversation conversation,
        string eventType,
        string text,
        string payload,
        OfficeLocation office,
        DateTimeOffset occurredAt,
        ICollection<ConversationMessage> messages,
        ICollection<Notification> notifications)
    {
        var key = $"demo:{occurredAt:yyyy-MM}:{office}:{eventType}";
        var message = new ConversationMessage
        {
            Id = StableId($"message:{key}"),
            Conversation = conversation,
            ConversationId = conversation.Id,
            Audience = MessageAudience.Office,
            Office = office,
            Kind = ConversationMessageKind.Notification,
            Text = text,
            PayloadJson = payload,
            CreatedAt = occurredAt
        };
        messages.Add(message);
        notifications.Add(new Notification
        {
            Id = StableId($"notification:{key}"),
            Office = office,
            EventType = eventType,
            PayloadJson = payload,
            DeduplicationKey = key,
            DeliveryState = NotificationDeliveryState.Sent,
            DeliveryAttempts = 1,
            CreatedAt = occurredAt,
            DeliveredAt = occurredAt,
            ReadAt = occurredAt,
            ConversationMessageId = message.Id
        });
    }

    private static void AddEmployeeEvent(
        Conversation conversation,
        string employeeId,
        string eventType,
        string text,
        string payload,
        DateTimeOffset occurredAt,
        bool read,
        ICollection<ConversationMessage> messages,
        ICollection<Notification> notifications)
    {
        var key = $"demo:{occurredAt:yyyy-MM}:employee:{employeeId}:{eventType}:{StableId(payload):N}";
        var message = new ConversationMessage
        {
            Id = StableId($"message:{key}"),
            Conversation = conversation,
            ConversationId = conversation.Id,
            RecipientEmployeeId = employeeId,
            Audience = MessageAudience.Employee,
            Kind = ConversationMessageKind.Notification,
            Text = text,
            PayloadJson = payload,
            CreatedAt = occurredAt
        };
        messages.Add(message);
        notifications.Add(new Notification
        {
            Id = StableId($"notification:{key}"),
            RecipientEmployeeId = employeeId,
            EventType = eventType,
            PayloadJson = payload,
            DeduplicationKey = key,
            DeliveryState = NotificationDeliveryState.Sent,
            DeliveryAttempts = 1,
            CreatedAt = occurredAt,
            DeliveredAt = occurredAt,
            ReadAt = read ? occurredAt : null,
            ConversationMessageId = message.Id
        });
    }

    private static void AddHrEvent(
        Conversation conversation,
        string eventType,
        string text,
        string payload,
        DateTimeOffset occurredAt,
        bool read,
        ICollection<ConversationMessage> messages,
        ICollection<Notification> notifications)
    {
        var key = $"demo:{occurredAt:yyyy-MM}:hr:{eventType}:{StableId(payload):N}";
        var message = new ConversationMessage
        {
            Id = StableId($"message:{key}"),
            Conversation = conversation,
            ConversationId = conversation.Id,
            Audience = MessageAudience.Hr,
            Kind = ConversationMessageKind.Notification,
            Text = text,
            PayloadJson = payload,
            CreatedAt = occurredAt
        };
        messages.Add(message);
        notifications.Add(new Notification
        {
            Id = StableId($"notification:{key}"),
            IsHrAudience = true,
            EventType = eventType,
            PayloadJson = payload,
            DeduplicationKey = key,
            DeliveryState = NotificationDeliveryState.Sent,
            DeliveryAttempts = 1,
            CreatedAt = occurredAt,
            DeliveredAt = occurredAt,
            ReadAt = read ? occurredAt : null,
            ConversationMessageId = message.Id
        });
    }

    private static async Task EnsureCurrentOpenRefillsAsync(
        SnackCounterDbContext database,
        IReadOnlyDictionary<(OfficeLocation Office, string Name), SnackItem> items,
        DateTimeOffset now)
    {
        var currentRefillIds = new[]
        {
            Guid.Parse("a1c34bd0-65a4-4d4f-83f5-e9d9783f61a1"),
            Guid.Parse("b2d45ce1-76b5-4e50-94a6-fa0a894072b2")
        };
        var currentRefills = await database.Refills.Where(refill => currentRefillIds.Contains(refill.Id))
            .ToDictionaryAsync(refill => refill.Id);
        if (!currentRefills.TryGetValue(currentRefillIds[0], out var osloRefill))
        {
            var item = items[(OfficeLocation.Oslo, "Coffee")];
            osloRefill = new Refill(currentRefillIds[0], OfficeLocation.Oslo, item.Name, now.AddHours(-2))
            {
                SnackItemId = item.Id
            };
            database.Refills.Add(osloRefill);
        }
        else if (osloRefill.SnackItemId is null)
        {
            osloRefill.SnackItemId = items[(OfficeLocation.Oslo, "Coffee")].Id;
        }

        if (!currentRefills.TryGetValue(currentRefillIds[1], out var bengaluruRefill))
        {
            var item = items[(OfficeLocation.Bengaluru, "Tea")];
            bengaluruRefill = new Refill(currentRefillIds[1], OfficeLocation.Bengaluru, item.Name, now.AddHours(-1))
            {
                SnackItemId = item.Id
            };
            database.Refills.Add(bengaluruRefill);
        }
        else if (bengaluruRefill.SnackItemId is null)
        {
            bengaluruRefill.SnackItemId = items[(OfficeLocation.Bengaluru, "Tea")].Id;
        }

        await database.SaveChangesAsync();
    }

    private static Guid StableId(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"SnackCounterDemo:{value}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static DateTimeOffset At(DateOnly month, int day, int hour) =>
        new(month.AddDays(day - 1).ToDateTime(new TimeOnly(hour, 0), DateTimeKind.Utc), TimeSpan.Zero);

    private static DateTimeOffset AtDate(DateOnly date, int hour, int minute) =>
        new(date.ToDateTime(new TimeOnly(hour, minute), DateTimeKind.Utc), TimeSpan.Zero);

    private sealed record HistoricalPurchase(
        OfficeLocation Office,
        string Item,
        string Unit,
        decimal OpeningQuantity,
        decimal RestockQuantity,
        decimal ConsumedQuantity,
        decimal UnitCost,
        string EmployeeId,
        int PurchaseDay,
        ReimbursementClaimStatus ClaimStatus);
}
