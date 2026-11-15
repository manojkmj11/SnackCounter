import { CommonModule, DatePipe } from '@angular/common';
import { Component, HostListener, OnDestroy, OnInit, computed, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { firstValueFrom } from 'rxjs';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';

const API = 'http://localhost:5121/api';
const HUB = 'http://localhost:5121/hubs/notifications';
type Office = 'Oslo' | 'Bengaluru';
type Currency = 'NOK' | 'INR';
type User = { employeeId: string; displayName: string; email: string; homeOffice: Office; payrollCurrency: Currency; roles: string[] };
type Snack = { id: string; office: Office; name: string; unit: string; quantityOnHand: number; reorderThreshold: number; isActive: boolean; isClaimed: boolean; rowVersion: string };
type Refill = { id: string; office: Office; item: string; status: string; assignedEmployeeId?: string; createdAt: string; assignmentExpiresAt?: string; purchaseAmount?: number; purchaseCurrency?: Currency; version: number; requesterEmployeeId?: string; snackItemId?: string; rowVersion: string };
type RefillOfferState = 'None' | 'Offered' | 'Acknowledged' | 'Accepted';
type OfficeRefill = { refill: Refill; itemRowVersion?: string; suggestedEmployeeId?: string | null; suggestionReason?: string; canAcceptOffer?: boolean; offerExpiresAt?: string | null; offerState?: RefillOfferState };
type Claim = { id: string; employeeId: string; office: Office; purchaseId: string; refillId?: string; submissionAttempt: number; previousClaimId?: string; amount: number; currency: Currency; description: string; status: string; processingByEmployeeId?: string; decisionReason?: string; submittedAt: string; hasReceipt: boolean; rowVersion: string };
type Purchase = { purchaseId: string; office: Office; item: string; amount: number; currency: Currency; purchasedAt: string; description: string; claimId?: string; claimStatus?: string; submissionAttempt?: number; decisionReason?: string };
type ClaimHistoryEntry = { purchaseKey: string; claim: Claim; attempts: number; wasDenied: boolean };
type Leave = { id: string; office: Office; startDate: string; endDate: string; status: string };
type Conversation = { id: string; kind: string; office?: Office; title: string; latestMessage?: Message; unreadCount: number };
type Message = { id: string; conversationId: string; senderEmployeeId?: string; kind: string; text?: string; createdAt: string };
type Notification = { id: string; eventType: string; recipientEmployeeId?: string; office?: Office; isHrAudience?: boolean; conversationMessageId?: string; payloadJson?: string; createdAt: string; readAt?: string };
type OfficeReport = { office: Office; currency: Currency; budget: number; spent: number; remaining: number; mostConsumedItems: { item: string; quantity: number }[] };
type PantryOverview = { office: Office; month: string; currency: Currency; budget: number; spent: number; remaining: number; budgetReached: boolean; topRefilledItems: { item: string; refillCount: number; quantityAdded: number }[] };
type ReportSnapshot = { offices: OfficeReport[]; totalsByCurrency: Partial<Record<Currency, number>>; asOf: string };
type OfficeSettings = { office: Office; budgetCurrency: Currency };
type DemoIdentity = { employeeId: string; name: string; email: string; role: string; office: Office };

const IDENTITIES: DemoIdentity[] = [
  { employeeId: 'oslo-01', name: 'Anika Rao', email: 'anika.rao@snackcounter.local', role: 'Employee', office: 'Oslo' },
  { employeeId: 'oslo-02', name: 'Erik Nilsen', email: 'erik.nilsen@snackcounter.local', role: 'Employee', office: 'Oslo' },
  { employeeId: 'blr-01', name: 'Meera Iyer', email: 'meera.iyer@snackcounter.local', role: 'Employee', office: 'Bengaluru' },
  { employeeId: 'blr-02', name: 'Kiran Shah', email: 'kiran.shah@snackcounter.local', role: 'Employee', office: 'Bengaluru' },
  { employeeId: 'hr-oslo', name: 'Nora Berg', email: 'nora.berg@snackcounter.local', role: 'HR', office: 'Oslo' },
  { employeeId: 'hr-blr', name: 'Ravi Menon', email: 'ravi.menon@snackcounter.local', role: 'HR', office: 'Bengaluru' },
  { employeeId: 'manager-01', name: 'Sofia Lind', email: 'sofia.lind@snackcounter.local', role: 'Manager', office: 'Oslo' },
];

@Component({
  imports: [CommonModule, FormsModule, DatePipe],
  selector: 'app-root',
  styleUrl: './app.css',
  templateUrl: './app.html',
})
export class App implements OnInit, OnDestroy {
  readonly identities = IDENTITIES;
  readonly user = signal<User | null>(null);
  readonly page = signal('overview');
  readonly busy = signal(false);
  readonly loading = signal(true);
  readonly error = signal('');
  readonly notice = signal('');
  readonly mobileNavOpen = signal(false);
  readonly inventory = signal<Snack[]>([]);
  readonly refills = signal<Refill[]>([]);
  readonly refillQueue = signal<OfficeRefill[]>([]);
  readonly reportEmptyOpen = signal(false);
  readonly newItemFormOpen = signal(false);
  readonly claims = signal<Claim[]>([]);
  readonly employeeClaims = signal<Claim[]>([]);
  readonly purchases = signal<Purchase[]>([]);
  readonly reimbursementReceipt = signal<File | null>(null);
  readonly leaves = signal<Leave[]>([]);
  readonly offices = signal<Office[]>(['Oslo', 'Bengaluru']);
  readonly officeCurrencies = signal<Record<Office, Currency>>({ Oslo: 'NOK', Bengaluru: 'INR' });
  readonly reports = signal<OfficeReport[]>([]);
  readonly monthlyOverview = signal<PantryOverview | null>(null);
  readonly reportTotals = signal<Partial<Record<Currency, number>>>({});
  readonly reportCurrencies: Currency[] = ['NOK', 'INR'];
  readonly conversations = signal<Conversation[]>([]);
  readonly messages = signal<Message[]>([]);
  readonly notifications = signal<Notification[]>([]);
  readonly activeConversation = signal<Conversation | null>(null);
  emptyReportItemId = '';
  readonly selectedOffice = signal<Office>('Oslo');
  readonly acceptedRefillVersions = signal<Record<string, number>>({});
  workingMode: 'WFO' | 'WFH' = 'WFO';
  workingOffice: Office = 'Oslo';
  readonly isHr = computed(() => this.user()?.roles.includes('HR') ?? false);
  readonly isManager = computed(() => this.user()?.roles.includes('Manager') ?? false);
  readonly assignedToMe = computed(() => this.refillQueue().map(x => x.refill).filter(x => x.status === 'Assigned' && this.isAssignedToMe(x)));
  readonly needsStock = computed(() => this.inventory().filter(x => x.isActive && x.quantityOnHand <= 0));
  readonly openRefillQueue = computed(() => this.refillQueue().filter(x => x.refill.status === 'Open'));
  readonly claimedRefillQueue = computed(() => this.refillQueue().filter(x => x.refill.status === 'Assigned'));
  readonly unreadCount = computed(() => this.notifications().filter(x => !x.readAt).length);
  readonly myClaims = computed(() => this.employeeClaims().filter(x => x.employeeId === this.user()?.employeeId));
  readonly claimHistory = computed<ClaimHistoryEntry[]>(() => {
    const purchases = this.purchases();
    const groups = new Map<string, Claim[]>();
    for (const claim of this.myClaims()) {
      let purchase = purchases.find(item => item.purchaseId === claim.purchaseId || item.purchaseId === claim.refillId);
      if (!purchase) {
        const note = this.normalizedPurchaseNote(claim.description);
        const candidates = purchases.filter(item => item.office === claim.office && item.amount === claim.amount && item.currency === claim.currency &&
          this.sameLocalDay(item.purchasedAt, claim.submittedAt) &&
          (note === this.normalizedPurchaseNote(item.description) || note === this.normalizedPurchaseNote(item.item)));
        if (candidates.length === 1) purchase = candidates[0];
      }
      const key = purchase?.purchaseId ?? claim.refillId ?? claim.purchaseId;
      const rows = groups.get(key) ?? [];
      rows.push(claim); groups.set(key, rows);
    }
    return [...groups].map(([purchaseKey, rows]) => {
      rows.sort((a, b) => new Date(b.submittedAt).getTime() - new Date(a.submittedAt).getTime());
      return { purchaseKey, claim: rows[0], attempts: rows.length, wasDenied: rows.some(row => row.status === 'Denied') };
    }).sort((a, b) => new Date(b.claim.submittedAt).getTime() - new Date(a.claim.submittedAt).getTime());
  });
  readonly activeClaimCount = computed(() => this.claimHistory().filter(x => ['Submitted', 'Processing', 'Approved'].includes(x.claim.status)).length);
  readonly selectedPurchase = computed(() => this.purchases().find(x => x.purchaseId === this.claimPurchaseId()));
  readonly selectedCurrency = computed<Currency>(() => this.officeCurrencies()[this.selectedOffice()]);
  readonly currentIdentity = computed(() => IDENTITIES.find(x => x.email === this.user()?.email));
  readonly todayNumber = new Date().toLocaleDateString(undefined, { day: '2-digit' });
  readonly todayMonth = new Date().toLocaleDateString(undefined, { month: 'long' });
  identityEmail = localStorage.getItem('snackcounter.email') ?? IDENTITIES[0].email;
  loginOffice: Office = (localStorage.getItem('snackcounter.office') as Office) || 'Oslo';
  refillQuantity = 1;
  refillAmount: number | null = null;
  refillDescription = '';
  readonly claimPurchaseId = signal('');
  claimDescription = '';
  leaveStart = '';
  leaveEnd = '';
  leaveOffice: Office = 'Oslo';
  chatText = '';
  newItemName = '';
  newPantryItemName = '';
  newItemUnit = 'pack';
  newItemThreshold = 3;
  month = new Date().toISOString().slice(0, 7);
  private hub?: HubConnection;

  constructor(private readonly http: HttpClient) {}

  async ngOnInit(): Promise<void> {
    const saved = localStorage.getItem('snackcounter.email');
    if (saved) { this.identityEmail = saved; await this.login(false); }
    else this.loading.set(false);
  }

  ngOnDestroy(): void { void this.hub?.stop(); }

  async login(showFeedback = true): Promise<void> {
    this.busy.set(true); this.error.set('');
    try {
      const response = await firstValueFrom(this.http.post<{ user: User; selectedWorkingOffice: Office }>(`${API}/auth/dev-login`, { email: this.identityEmail, workingOffice: this.loginOffice }));
      localStorage.setItem('snackcounter.email', this.identityEmail);
      localStorage.setItem('snackcounter.office', response.selectedWorkingOffice);
      this.user.set(response.user);
      this.selectedOffice.set(response.selectedWorkingOffice);
      this.loginOffice = response.selectedWorkingOffice;
      this.leaveOffice = response.selectedWorkingOffice;
      this.page.set('overview');
      await this.refreshAll();
      await this.connectHub();
    } catch (error) { this.setError(error, 'Could not sign in. Please try again.'); }
    finally { this.loading.set(false); this.busy.set(false); }
  }

  async refreshAll(): Promise<void> {
    const u = this.user(); if (!u) return;
    this.loading.set(true); this.error.set('');
    const office = this.selectedOffice();
    const tasks: Promise<void>[] = [
      this.get<Snack[]>(`offices/${office}/inventory`).then(v => this.inventory.set(v)),
      this.get<OfficeRefill[]>(`offices/${office}/refill-requests`).then(v => this.refillQueue.set(v)),
      this.get<PantryOverview>(`offices/${office}/monthly-overview`).then(v => this.monthlyOverview.set(v)),
      this.get<Refill[]>(`me/refills?office=${encodeURIComponent(office)}`).then(v => this.refills.set(v)),
      this.get<Claim[]>('me/claims').then(v => this.employeeClaims.set(v)),
      this.get<Purchase[]>('me/purchases').then(v => this.purchases.set(v)),
      this.get<Leave[]>(`me/leaves?office=${encodeURIComponent(office)}`).then(v => this.leaves.set(v)),
      this.get<Notification[]>(`me/notifications?office=${encodeURIComponent(office)}&pageSize=50`).then(v => this.notifications.set(v)),
      this.get<Conversation[]>(`me/conversations?office=${encodeURIComponent(office)}`).then(v => this.conversations.set(v)),
      this.get<OfficeSettings[]>('offices').then(v => {
        this.offices.set(v.map(x => x.office));
        this.officeCurrencies.set(Object.fromEntries(v.map(x => [x.office, x.budgetCurrency])) as Record<Office, Currency>);
      }),
    ];
    if (u.roles.includes('HR')) tasks.push(this.get<Claim[]>('hr/claims').then(v => this.claims.set(v)));
    if (u.roles.some(x => ['HR', 'Manager'].includes(x))) tasks.push(this.get<ReportSnapshot>(`reports/snapshot?month=${this.month}`).then(v => { this.reports.set(v.offices); this.reportTotals.set(v.totalsByCurrency); }));
    const results = await Promise.allSettled(tasks);
    const failed = results.find(x => x.status === 'rejected') as PromiseRejectedResult | undefined;
    if (failed) this.setError(failed.reason, 'We could not load the pantry just now. Check your connection and try again.');
    this.loading.set(false);
  }

  async changeOffice(office: Office): Promise<void> {
    if (office === this.selectedOffice()) return;
    this.selectedOffice.set(office); this.loginOffice = office; localStorage.setItem('snackcounter.office', office);
    this.inventory.set([]); this.refillQueue.set([]); this.refills.set([]); this.claims.set([]); this.employeeClaims.set([]); this.leaves.set([]);
    this.monthlyOverview.set(null);
    this.notifications.set([]); this.conversations.set([]); this.activeConversation.set(null); this.messages.set([]);
    await this.refreshAll();
  }

  async reportItemEmpty(item: Snack): Promise<void> {
    await this.mutate(async () => {
      await this.post('me/chat/report-empty', { office: this.selectedOffice(), snackItemId: item.id, itemRowVersion: item.rowVersion });
      this.reportEmptyOpen.set(false); this.newItemName = ''; this.flash(`${item.name} was reported empty.`); await this.refreshAll();
    });
  }

  async reportEmptyItemById(itemId: string): Promise<void> {
    const item = this.inventory().find(candidate => candidate.id === itemId);
    if (!item) { this.error.set('Choose an item from this pantry first.'); return; }
    await this.reportItemEmpty(item);
  }

  async requestRefillByName(): Promise<void> {
    const name = this.newItemName.trim();
    if (!name) { this.error.set('Enter the name of the empty pantry item.'); return; }
    const item = this.inventory().find(x => x.name.toLocaleLowerCase() === name.toLocaleLowerCase() && x.isActive);
    if (!item) { this.error.set('That item is not in this pantry catalog.'); return; }
    await this.reportItemEmpty(item);
    this.newItemName = '';
  }

  async addItem(): Promise<void> {
    if (!this.newPantryItemName.trim()) return;
    await this.mutate(async () => {
      await this.post(`offices/${this.selectedOffice()}/inventory/items`, { name: this.newPantryItemName.trim(), unit: this.newItemUnit, reorderThreshold: Number(this.newItemThreshold) });
      const inventory = await this.get<Snack[]>(`offices/${this.selectedOffice()}/inventory`);
      const item = inventory.find(candidate => candidate.name.toLocaleLowerCase() === this.newPantryItemName.trim().toLocaleLowerCase());
      if (!item) throw new Error('The item was created but could not be found in this pantry. Refresh and try again.');
      await this.post(`offices/${this.selectedOffice()}/refill-requests`, { snackItemId: item.id });
      this.newPantryItemName = ''; this.newItemFormOpen.set(false); this.flash('New pantry item added to the refill queue.'); await this.refreshAll();
    });
  }

  async claimRefill(refill: Refill): Promise<void> {
    const row = this.refillQueue().find(x => x.refill.id === refill.id);
    if (!row?.itemRowVersion) { this.error.set('This refill changed. Refresh the queue and try again.'); await this.refreshAll(); return; }
    await this.mutate(async () => {
      await this.post(`offices/${this.selectedOffice()}/refill-requests/${refill.id}/claim`, { refillRowVersion: refill.rowVersion, itemRowVersion: row.itemRowVersion });
      this.acceptedRefillVersions.update(versions => { const next = { ...versions }; delete next[refill.id]; return next; });
      this.workingOffice = this.selectedOffice();
      this.flash(`${refill.item} is yours to refill.`); await this.refreshAll();
    }, 'Another employee claimed this refill first. The queue has been refreshed; choose another open item.');
  }

  async acceptRefill(refill: Refill): Promise<void> {
    await this.mutate(async () => {
      const result = await this.post<{ refill: Refill; accepted: boolean; skipReason?: string }>(`refills/${refill.id}/accept`, {
        expectedVersion: refill.version, workingMode: this.workingMode, workingOffice: this.workingOffice
      });
      if (result.accepted) {
        this.acceptedRefillVersions.update(versions => ({ ...versions, [refill.id]: result.refill.version }));
        this.flash('You’re at the office and ready to refill this item.');
      } else {
        this.acceptedRefillVersions.update(versions => { const next = { ...versions }; delete next[refill.id]; return next; });
        this.flash(result.skipReason || 'This refill is back in the shared queue.');
      }
      await this.refreshAll();
    });
  }

  async completeRefill(refill: Refill): Promise<void> {
    if (this.acceptedRefillVersions()[refill.id] !== refill.version) { this.error.set('Confirm your work location before recording this refill.'); return; }
    if (this.refillQuantity <= 0 || this.refillAmount == null || this.refillAmount <= 0 || !this.refillDescription.trim()) { this.error.set('Enter the quantity, purchase total, and a short purchase note.'); return; }
    await this.mutate(async () => {
      await this.post(`refills/${refill.id}/complete`, { expectedVersion: refill.version, quantity: Number(this.refillQuantity), amount: Number(this.refillAmount), description: this.refillDescription.trim(), receiptImageBase64: null });
      this.acceptedRefillVersions.update(versions => { const next = { ...versions }; delete next[refill.id]; return next; });
      this.flash(`${refill.item} was restocked.`); this.refillDescription = ''; this.refillAmount = null; await this.refreshAll();
    });
  }

  async submitClaim(): Promise<void> {
    const purchase = this.selectedPurchase();
    if (!purchase) { this.error.set('Choose one of your purchases before filing a reimbursement claim.'); return; }
    const currentStatus = this.purchaseClaimStatus(purchase);
    if (currentStatus && currentStatus !== 'Denied') { this.error.set('This purchase already has a claim in progress.'); return; }
    if (purchase.amount <= 0 || !this.claimDescription.trim()) { this.error.set('Add a short note about this purchase.'); return; }
    await this.mutate(async () => {
      const receipt = this.reimbursementReceipt();
      if (receipt && receipt.size > 5 * 1024 * 1024) throw new Error('Receipt must be 5 MB or smaller.');
      const receiptBase64 = receipt ? await this.fileBase64(receipt) : null;
      const resubmission = currentStatus === 'Denied';
      await this.post('me/claims', { purchaseId: purchase.purchaseId, idempotencyKey: purchase.purchaseId, refillId: purchase.purchaseId, office: purchase.office, amount: purchase.amount, currency: purchase.currency, description: this.claimDescription.trim(), receiptBase64, receiptMediaType: receipt?.type || null });
      this.clearClaimForm(); this.flash(resubmission ? 'Claim resubmitted to HR.' : 'Claim submitted to HR.'); await this.refreshAll();
    });
  }

  startPurchaseClaim(purchase: Purchase): void {
    const currentStatus = this.purchaseClaimStatus(purchase);
    if (currentStatus && currentStatus !== 'Denied') return;
    this.claimPurchaseId.set(purchase.purchaseId);
    this.claimDescription = purchase.description;
    this.reimbursementReceipt.set(null);
    this.page.set('claims'); this.error.set('');
  }

  clearClaimForm(): void {
    this.claimPurchaseId.set(''); this.claimDescription = ''; this.reimbursementReceipt.set(null);
  }

  purchaseClaimLabel(purchase: Purchase): string {
    const entry = this.claimHistory().find(item => item.purchaseKey === purchase.purchaseId);
    if (entry) return this.claimHistoryStatus(entry);
    const status = this.purchaseClaimStatus(purchase);
    if (!status) return 'Not claimed';
    if (status === 'Submitted' && (purchase.submissionAttempt ?? 1) > 1) return 'Resubmitted';
    return this.statusLabel(status);
  }

  purchaseClaimStatus(purchase: Purchase): string | undefined {
    return this.claimHistory().find(item => item.purchaseKey === purchase.purchaseId)?.claim.status ?? purchase.claimStatus;
  }

  claimHistoryStatus(entry: ClaimHistoryEntry): string {
    if (entry.attempts > 1 && ['Submitted', 'Processing', 'Approved'].includes(entry.claim.status)) return 'Resubmitted';
    return this.statusLabel(entry.claim.status);
  }

  private normalizedPurchaseNote(value: string): string { return value.trim().toLocaleLowerCase().replace(/\s+/g, ' '); }
  private sameLocalDay(first: string, second: string): boolean {
    const a = new Date(first); const b = new Date(second);
    return a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
  }

  openChatClaimForm(): void {
    this.navigate('claims');
  }

  selectReceipt(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0] ?? null;
    if (file && file.size > 5 * 1024 * 1024) { this.error.set('Receipt must be 5 MB or smaller.'); input.value = ''; this.reimbursementReceipt.set(null); return; }
    this.reimbursementReceipt.set(file);
  }

  async createLeave(): Promise<void> {
    if (!this.leaveStart || !this.leaveEnd) { this.error.set('Choose the first and last day of leave.'); return; }
    await this.mutate(async () => {
      await this.post('me/leaves', { office: this.leaveOffice, startDate: this.leaveStart, endDate: this.leaveEnd });
      this.flash('Planned leave saved.'); this.leaveStart = ''; this.leaveEnd = ''; await this.refreshAll();
    });
  }

  @HostListener('document:keydown.escape')
  closeMobileNavigation(): void {
    if (this.mobileNavOpen()) {
      this.mobileNavOpen.set(false);
      window.setTimeout(() => document.querySelector<HTMLButtonElement>('.mobile-menu')?.focus(), 0);
    }
  }

  async cancelLeave(leave: Leave): Promise<void> {
    await this.mutate(async () => { await this.request<void>('DELETE', `me/leaves/${leave.id}`); this.flash('Planned leave cancelled.'); await this.refreshAll(); });
  }

  async takeClaim(claim: Claim): Promise<void> {
    await this.mutate(async () => { await this.post(`hr/claims/${claim.id}/claim`, { rowVersion: claim.rowVersion }); this.flash('Claim assigned to you for review.'); await this.refreshAll(); });
  }

  async decideClaim(claim: Claim, approve: boolean): Promise<void> {
    const reason = approve ? '' : window.prompt('Reason for declining this claim:')?.trim();
    if (!approve && !reason) return;
    await this.mutate(async () => { await this.post(`hr/claims/${claim.id}/decision`, { rowVersion: claim.rowVersion, approve, reason }); this.flash(approve ? 'Claim approved.' : 'Claim declined.'); await this.refreshAll(); });
  }

  async reimburseClaim(claim: Claim): Promise<void> {
    await this.mutate(async () => {
      await this.post(`hr/claims/${claim.id}/reimburse`, { rowVersion: claim.rowVersion, payrollAmount: claim.amount, payrollCurrency: claim.currency });
      this.flash('Claim marked as reimbursed.'); await this.refreshAll();
    });
  }

  async openReceipt(claim: Claim): Promise<void> {
    try {
      const blob = await firstValueFrom(this.http.get(`${API}/hr/claims/${claim.id}/receipt`, { headers: this.headers(), responseType: 'blob' }));
      const url = URL.createObjectURL(blob);
      window.open(url, '_blank', 'noopener');
      window.setTimeout(() => URL.revokeObjectURL(url), 60_000);
    } catch (error) { this.setError(error, 'The receipt could not be opened.'); }
  }

  async openConversation(conversation: Conversation): Promise<void> {
    this.activeConversation.set(conversation); this.messages.set([]);
    try { const page = await this.get<{ items: Message[] }>(`conversations/${conversation.id}/messages`); this.messages.set(page.items); }
    catch (error) { this.setError(error, 'Messages could not be loaded.'); }
  }

  async sendMessage(): Promise<void> {
    const conversation = this.activeConversation(); if (!conversation || !this.chatText.trim()) return;
    await this.mutate(async () => {
      const message = await this.post<Message>(`conversations/${conversation.id}/messages`, { text: this.chatText.trim() });
      this.messages.update(list => [...list, message]); this.chatText = '';
    });
  }

  async loadReports(): Promise<void> {
    await this.mutate(async () => {
      const data = await this.get<ReportSnapshot>(`reports/snapshot?month=${this.month}`);
      this.reports.set(data.offices); this.reportTotals.set(data.totalsByCurrency);
    });
  }

  async logout(): Promise<void> { await this.hub?.stop(); this.user.set(null); this.refills.set([]); this.refillQueue.set([]); this.inventory.set([]); this.page.set('overview'); }

  navigate(page: string): void { this.page.set(page); this.mobileNavOpen.set(false); this.error.set(''); if (page === 'chat' && this.conversations().length && !this.activeConversation()) void this.openConversation(this.conversations()[0]); }
  isActive(page: string): boolean { return this.page() === page; }
  officeName(office: Office): string { return office === 'Bengaluru' ? 'Bengaluru' : 'Oslo'; }
  hasRefillRequest(item: Snack): boolean { return this.refillQueue().some(row => row.refill.snackItemId === item.id); }
  refillForItem(item: Snack): Refill | undefined { return this.refillQueue().find(row => row.refill.snackItemId === item.id)?.refill; }
  isAssignedToMe(refill: Refill): boolean { return !!refill.assignedEmployeeId && refill.assignedEmployeeId.toLocaleLowerCase() === this.user()?.employeeId.toLocaleLowerCase(); }
  refillClaimLabel(refill?: Refill): string {
    if (!refill?.assignedEmployeeId) return 'Waiting for the next offer';
    if (this.isAssignedToMe(refill)) return 'Offered to you';
    const claimant = this.identities.find(identity => identity.employeeId.toLocaleLowerCase() === refill.assignedEmployeeId!.toLocaleLowerCase());
    return claimant ? `Offered to ' + claimant.name` : 'Offered to another employee';
  }
  refillOfferState(refillId: string): RefillOfferState {
    return this.refillQueue().find(entry => entry.refill.id === refillId)?.offerState ?? 'None';
  }
  refillQueueLabel(entry: OfficeRefill): string {
    if (entry.offerState === 'Acknowledged' && this.isAssignedToMe(entry.refill)) return 'Reserved for you';
    return this.refillClaimLabel(entry.refill);
  }
  formatMoney(amount: number | undefined, currency: Currency | undefined): string { const unit = currency ?? this.selectedCurrency(); return new Intl.NumberFormat(unit === 'NOK' ? 'en-NO' : 'en-IN', { style: 'currency', currency: unit, maximumFractionDigits: 2 }).format(amount ?? 0); }
  monthlyBudgetUsePercent(): number { const overview = this.monthlyOverview(); return overview?.budget ? Math.min(100, overview.spent / overview.budget * 100) : 0; }
  shortDate(value?: string): string { return value ? new Date(value).toLocaleDateString(undefined, { day: 'numeric', month: 'short' }) : '–'; }
  statusLabel(value: string): string { return value.replace(/([a-z])([A-Z])/g, '$1 $2'); }
  initials(name?: string): string { return (name ?? 'SC').split(' ').map(x => x[0]).join('').slice(0, 2).toUpperCase(); }
  isMyClaim(claim: Claim): boolean { return claim.employeeId === this.user()?.employeeId; }
  isCurrentProcessor(claim: Claim): boolean { return claim.processingByEmployeeId === this.user()?.employeeId; }
  async markRead(notification: Notification): Promise<void> { if (!notification.readAt) { await this.request<void>('POST', `me/notifications/${notification.id}/read`, {}); this.notifications.update(list => list.map(x => x.id === notification.id ? { ...x, readAt: new Date().toISOString() } : x)); } }

  notificationTitle(notification: Notification): string {
    const titles: Record<string, string> = {
      RefillAssigned: 'A refill has your name on it',
      RefillCompleted: 'A shelf has been restocked',
      RefillDenied: 'A refill request needs another look',
      EmptyItemReported: 'Empty item reported',
      StockDepleted: 'A pantry item has run out',
      MonthlyReportReady: 'Monthly pantry report is ready',
      MonthlyConsumptionRanked: 'This month’s most used items',
      BudgetThresholdReached: 'Pantry budget is getting close',
      BudgetExhausted: 'Pantry budget has been used',
      BudgetExceeded: 'Pantry budget was exceeded',
      BudgetMet: 'Pantry budget was fully used',
      BudgetUnusedAtClose: 'Pantry budget closed with funds left',
      ClaimSubmitted: 'A reimbursement is waiting for review',
      ClaimDecided: 'There’s an update on your reimbursement',
      ClaimReimbursed: 'Your reimbursement has been paid',
      HRChatMessage: 'A new message in the HR chat',
      OfficeChatMessage: 'A new message in the office chat',
    };
    return titles[notification.eventType] ?? this.statusLabel(notification.eventType);
  }

  notificationMark(notification: Notification): string {
    const event = notification.eventType;
    if (event.includes('Refill')) return 'R';
    if (event.includes('Claim')) return 'C';
    if (event.includes('Report') || event.includes('Budget') || event === 'MonthlyConsumptionRanked') return 'B';
    if (event.includes('ChatMessage')) return 'M';
    return 'S';
  }

  notificationSummary(notification: Notification): string {
    const payload = this.notificationPayload(notification);
    const office = this.notificationOffice(payload.office ?? notification.office);
    const month = this.notificationMonth(payload.month);
    const currency = this.notificationCurrency(payload.currency) || this.officeCurrency(office);
    const money = (amount: unknown) => typeof amount === 'number' ? this.formatEventMoney(amount, currency) : '';
    switch (notification.eventType) {
      case 'RefillCompleted': {
        const item = this.inventory().find(candidate => candidate.id === payload.itemId)?.name;
        const quantity = typeof payload.quantity === 'number' ? `${payload.quantity} added` : '';
        const total = money(payload.amount);
        return [item, quantity, total && `${total} spent`, office].filter(Boolean).join(' · ') || 'The pantry inventory has been updated.';
      }
      case 'RefillAssigned':
        return [payload.office && this.notificationOffice(payload.office), payload.expiresAt && `Please respond by ${new Date(payload.expiresAt).toLocaleString()}`].filter(Boolean).join(' · ') || 'Open refill work to respond to your assignment.';
      case 'EmptyItemReported':
        return [office, payload.itemName, typeof payload.budgetRemaining === 'number' && currency ? `${money(payload.budgetRemaining)} remaining` : ''].filter(Boolean).join(' · ') || 'Your empty-item report was sent to the pantry queue.';
      case 'MonthlyReportReady':
        if (Array.isArray(payload.offices)) return `${month ? `${month} · ` : ''}Combined office report with separate currency totals.`;
        return [month, office, money(payload.budget) && `${money(payload.budget)} budget`, money(payload.spent) && `${money(payload.spent)} spent`, money(payload.remaining) && `${money(payload.remaining)} remaining`].filter(Boolean).join(' · ') || 'Open monthly reports for the office breakdown.';
      case 'MonthlyConsumptionRanked': {
        const items = Array.isArray(payload.topItems)
          ? [...payload.topItems].sort((a: { quantity?: number }, b: { quantity?: number }) => Math.abs(b.quantity ?? 0) - Math.abs(a.quantity ?? 0)).map((item: { item?: string }) => item.item).filter(Boolean).slice(0, 3).join(', ')
          : '';
        return [month, office, items && `Most used: ${items}`].filter(Boolean).join(' · ') || 'Open monthly reports to see the pantry rankings.';
      }
      case 'BudgetThresholdReached':
      case 'BudgetExhausted':
      case 'BudgetExceeded':
      case 'BudgetMet':
      case 'BudgetUnusedAtClose':
        return [month && `${month} close`, office, money(payload.spent) && `${money(payload.spent)} spent`, money(payload.remaining) && `${money(payload.remaining)} remaining`].filter(Boolean).join(' · ') || 'Open monthly reports for the budget details.';
      case 'ClaimSubmitted':
        return [office, money(payload.amount)].filter(Boolean).join(' · ') || 'Open the HR claim desk to review this request.';
      case 'ClaimDecided':
      case 'ClaimReimbursed':
        return [typeof payload.status === 'number' ? ['Submitted', 'Processing', 'Approved', 'Declined', 'Reimbursed'][payload.status] : payload.status, money(payload.payrollAmount ?? payload.amount), payload.reason].filter(Boolean).join(' · ') || 'Open reimbursements to see the latest status.';
      case 'StockDepleted':
        return [office, payload.itemName].filter(Boolean).join(' · ') || 'Check inventory and request a refill if needed.';
      case 'RefillDenied':
        return [office, payload.reason].filter(Boolean).join(' · ') || 'Open refill work to see what needs attention.';
      case 'HRChatMessage':
      case 'OfficeChatMessage':
        return office ? `${office} · Open the conversation to read the message.` : 'Open chat to read the new message.';
      default:
        return 'Open Snack Counter to see the latest activity.';
    }
  }

  async openNotification(notification: Notification): Promise<void> {
    try { await this.markRead(notification); } catch (error) { this.setError(error, 'This update could not be marked as read.'); }
    const event = notification.eventType;
    if (event === 'RefillAssigned') this.navigate('requests');
    else if (event.startsWith('Refill') || event === 'StockDepleted') {
      if (notification.office) await this.changeOffice(notification.office);
      this.navigate('requests');
    }
    else if (event.includes('Claim')) this.navigate(this.isHr() ? 'hr' : 'claims');
    else if (event.includes('Report') || event.includes('Budget') || event === 'MonthlyConsumptionRanked') {
      if (this.isHr() || this.isManager()) this.navigate('reports');
      else {
        if (notification.office) await this.changeOffice(notification.office);
        this.navigate('requests');
      }
    }
    else if (event.includes('ChatMessage')) {
      this.navigate('chat');
      const target = notification.isHrAudience
        ? this.conversations().find(conversation => conversation.kind === 'Hr')
        : this.conversations().find(conversation => conversation.kind === 'Office' && conversation.office === notification.office);
      if (target) await this.openConversation(target);
    }
  }

  private async connectHub(): Promise<void> {
    await this.hub?.stop();
    this.hub = new HubConnectionBuilder().withUrl(HUB, { accessTokenFactory: () => this.user()?.email ?? '' }).withAutomaticReconnect().configureLogging(LogLevel.Warning).build();
    this.hub.on('officeQueueChanged', (change: { office: Office }) => {
      if (change.office === this.selectedOffice()) void this.reloadOfficePantry();
    });
    this.hub.onreconnected(() => void this.reloadOfficePantry());
    this.hub.on('notification', (notification: Notification) => {
      const isPersonal = notification.recipientEmployeeId === this.user()?.employeeId;
      const isHrInbox = notification.isHrAudience === true && this.isHr();
      if ((!notification.office || notification.office === this.selectedOffice() || isPersonal || isHrInbox) && !this.notifications().some(x => x.id === notification.id)) this.notifications.update(items => [notification, ...items]);
      if (isPersonal && notification.eventType.startsWith('Claim')) {
        void this.get<Claim[]>('me/claims').then(items => this.employeeClaims.set(items)).catch(() => undefined);
        void this.get<Purchase[]>('me/purchases').then(items => this.purchases.set(items)).catch(() => undefined);
      }
      if (isHrInbox) void this.get<Claim[]>('hr/claims').then(items => this.claims.set(items)).catch(() => undefined);
      if (notification.office === this.selectedOffice() && ['RefillNeeded', 'RefillClaimed', 'RefillAvailable', 'RefillOffered', 'RefillOfferPassed', 'RefillOfferExpired', 'RefillAssignmentExpired', 'RefillReoffered', 'RefillCompleted', 'RefillDenied', 'StockDepleted', 'BudgetThresholdReached', 'BudgetMet', 'BudgetExceeded'].includes(notification.eventType)) {
        void this.reloadOfficePantry();
      }
      if (this.page() === 'chat') void this.reloadActiveMessages();
    });
    try { await this.hub.start(); } catch { /* The inbox remains available from the updates page. */ }
  }
  private async reloadOfficePantry(): Promise<void> {
    const office = this.selectedOffice();
    try {
      const [inventory, queue, overview] = await Promise.all([
        this.get<Snack[]>(`offices/${office}/inventory`),
        this.get<OfficeRefill[]>(`offices/${office}/refill-requests`),
        this.get<PantryOverview>(`offices/${office}/monthly-overview`),
      ]);
      if (office === this.selectedOffice()) { this.inventory.set(inventory); this.refillQueue.set(queue); this.monthlyOverview.set(overview); }
    } catch { /* Keep the last pantry view; manual refresh remains available. */ }
  }
  private async reloadActiveMessages(): Promise<void> { const c = this.activeConversation(); if (c) { try { const page = await this.get<{ items: Message[] }>(`conversations/${c.id}/messages`); this.messages.set(page.items); } catch { /* Keep the last visible history until connectivity returns. */ } } }
  private async get<T>(path: string): Promise<T> { return firstValueFrom(this.http.get<T>(`${API}/${path}`, { headers: this.headers() })); }
  private async post<T = unknown>(path: string, body: unknown): Promise<T> { return firstValueFrom(this.http.post<T>(`${API}/${path}`, body, { headers: this.headers() })); }
  private async request<T>(method: string, path: string, body?: unknown): Promise<T> { return firstValueFrom(this.http.request<T>(method, `${API}/${path}`, { body, headers: this.headers() })); }
  private headers(): Record<string, string> { return this.user() ? { 'X-Demo-Email': this.user()!.email } : {}; }
  private async fileBase64(file: File): Promise<string> {
    const dataUrl = await new Promise<string>((resolve, reject) => {
      const reader = new FileReader(); reader.onerror = () => reject(new Error('Receipt could not be read.'));
      reader.onload = () => resolve(String(reader.result)); reader.readAsDataURL(file);
    });
    return dataUrl.slice(dataUrl.indexOf(',') + 1);
  }
  private notificationPayload(notification: Notification): any {
    try { const value = notification.payloadJson ? JSON.parse(notification.payloadJson) : {}; return value && typeof value === 'object' ? value : {}; }
    catch { return {}; }
  }
  private notificationOffice(value: unknown): string {
    if (value === 'Oslo' || value === 0) return 'Oslo';
    if (value === 'Bengaluru' || value === 1) return 'Bengaluru';
    return '';
  }
  private notificationCurrency(value: unknown): Currency | '' {
    if (value === 'NOK' || value === 0) return 'NOK';
    if (value === 'INR' || value === 1) return 'INR';
    return '';
  }
  private officeCurrency(office: string): Currency | '' {
    return office === 'Oslo' || office === 'Bengaluru' ? this.officeCurrencies()[office] : '';
  }
  private notificationMonth(value: unknown): string {
    if (typeof value !== 'string' || !/^\d{4}-\d{2}$/.test(value)) return '';
    return new Date(`${value}-01T12:00:00`).toLocaleDateString(undefined, { month: 'long', year: 'numeric' });
  }
  private formatEventMoney(amount: number, currency: Currency | ''): string {
    if (!currency) return 'Currency unavailable';
    const value = new Intl.NumberFormat(currency === 'NOK' ? 'en-NO' : 'en-IN', { maximumFractionDigits: 2 }).format(amount);
    return `${currency} ${value}`;
  }
  private async mutate(action: () => Promise<void>, conflictFallback = 'That record changed while you were working. The latest information has been loaded; review it and try again.'): Promise<void> {
    this.busy.set(true); this.error.set('');
    try { await action(); }
    catch (error) {
      if (error instanceof HttpErrorResponse && error.status === 409) await this.refreshAll();
      this.setError(error, error instanceof HttpErrorResponse && error.status === 409 ? conflictFallback : 'That action could not be completed.');
    } finally { this.busy.set(false); }
  }
  private setError(error: unknown, fallback: string): void {
    if (!(error instanceof HttpErrorResponse)) {
      this.error.set(error instanceof Error && error.message ? error.message : fallback);
      return;
    }

    const problem = this.problemMessage(error);
    if (problem) {
      const isRefillRace = error.status === 409 && /another employee claimed this refill/i.test(problem);
      this.error.set(isRefillRace
        ? 'This refill was just claimed by a teammate. The queue is up to date; choose another open item.'
        : problem);
      return;
    }

    const friendlyByStatus: Record<number, string> = {
      0: 'Snack Counter could not reach the server. Check that the API is running, then try again.',
      400: 'Some of the information needs attention. Review the form and try again.',
      401: 'Your session has expired. Sign in again to continue.',
      403: 'You do not have access to do that.',
      404: 'That item or request is no longer available. Refresh the page and try again.',
      409: fallback,
      413: 'That file is too large. Choose a file under 5 MB and try again.',
      429: 'Too many requests were sent at once. Wait a moment and try again.'
    };
    this.error.set(friendlyByStatus[error.status] ?? (error.status >= 500
      ? 'The server could not complete that request. Try again in a moment.'
      : fallback));
  }

  private problemMessage(error: HttpErrorResponse): string | undefined {
    const body: unknown = error.error;
    if (body && typeof body === 'object') {
      const detail = (body as { detail?: unknown; title?: unknown }).detail ?? (body as { title?: unknown }).title;
      return typeof detail === 'string' && detail.trim() ? detail.trim() : undefined;
    }
    if (typeof body === 'string') {
      try {
        const parsed: unknown = JSON.parse(body);
        if (parsed && typeof parsed === 'object') {
          const detail = (parsed as { detail?: unknown; title?: unknown }).detail ?? (parsed as { title?: unknown }).title;
          return typeof detail === 'string' && detail.trim() ? detail.trim() : undefined;
        }
      } catch { /* Non-JSON error bodies are replaced with a user-facing status message. */ }
    }
    return undefined;
  }
  private flash(message: string): void { this.notice.set(message); window.setTimeout(() => { if (this.notice() === message) this.notice.set(''); }, 4200); }
}
