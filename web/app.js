const ORDER_API = "http://localhost:5001/api/v1";
const STOCK_API = "http://localhost:5002";

const PRODUCTS = {
  "APPLE-1": { name: "Apple", unitPrice: 1.5 },
  "BANANA-1": { name: "Banana", unitPrice: 0.75 },
  "MANGO-1": { name: "Mango", unitPrice: 3 },
  "DURIAN-1": { name: "Durian", unitPrice: 8 },
};

let token = localStorage.getItem("orderflow_token") || null;
let email = localStorage.getItem("orderflow_email") || null;

const $ = (id) => document.getElementById(id);

function setAuth(newToken, newEmail) {
  token = newToken;
  email = newEmail;
  if (token) {
    localStorage.setItem("orderflow_token", token);
    localStorage.setItem("orderflow_email", email);
  } else {
    localStorage.removeItem("orderflow_token");
    localStorage.removeItem("orderflow_email");
  }
  renderAuthState();
}

function renderAuthState() {
  const loggedIn = !!token;
  $("auth-logged-out").classList.toggle("hidden", loggedIn);
  $("auth-logged-in").classList.toggle("hidden", !loggedIn);
  if (loggedIn) $("auth-email").textContent = email;
  if (!loggedIn) {
    $("orders-body").innerHTML = '<tr><td colspan="5" class="empty">Log in and place an order to see it here.</td></tr>';
    $("order-detail").classList.add("hidden");
  }
}

function showMessage(text, ok) {
  const el = $("auth-message");
  el.textContent = text;
  el.className = "message " + (ok ? "ok" : "err");
}

async function authRequest(path, body) {
  const res = await fetch(`${ORDER_API}${path}`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.title || data.message || `HTTP ${res.status}`);
  return data;
}

$("login-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  const form = new FormData(e.target);
  try {
    const result = await authRequest("/auth/login", {
      email: form.get("email"),
      password: form.get("password"),
    });
    setAuth(result.token, form.get("email"));
    showMessage("Logged in.", true);
  } catch (err) {
    showMessage(err.message, false);
  }
});

$("register-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  const form = new FormData(e.target);
  try {
    const result = await authRequest("/auth/register", {
      email: form.get("email"),
      password: form.get("password"),
    });
    setAuth(result.token, form.get("email"));
    showMessage("Registered and logged in.", true);
  } catch (err) {
    showMessage(err.message, false);
  }
});

$("logout-btn").addEventListener("click", () => {
  setAuth(null, null);
  showMessage("Logged out.", true);
});

document.querySelectorAll(".tab").forEach((tab) => {
  tab.addEventListener("click", () => {
    document.querySelectorAll(".tab").forEach((t) => t.classList.remove("active"));
    document.querySelectorAll(".tab-panel").forEach((p) => p.classList.remove("active"));
    tab.classList.add("active");
    $(`${tab.dataset.tab}-form`).classList.add("active");
  });
});

// ---- Order form ----

function applyProductDefaults() {
  const id = $("product-select").value;
  const isCustom = id === "__custom";
  $("custom-product-label").classList.toggle("hidden", !isCustom);
  if (!isCustom && PRODUCTS[id]) {
    $("product-name").value = PRODUCTS[id].name;
    $("product-price").value = PRODUCTS[id].unitPrice;
  }
}
$("product-select").addEventListener("change", applyProductDefaults);
applyProductDefaults();

function showOrderResult(obj) {
  const el = $("order-result");
  el.textContent = typeof obj === "string" ? obj : JSON.stringify(obj, null, 2);
  el.classList.remove("hidden");
}

async function placeOrder(items, overrideToken) {
  const res = await fetch(`${ORDER_API}/orders`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      Authorization: `Bearer ${overrideToken !== undefined ? overrideToken : token}`,
    },
    body: JSON.stringify({ items }),
  });
  const text = await res.text();
  let data;
  try { data = JSON.parse(text); } catch { data = text; }
  return { status: res.status, data };
}

$("order-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  if (!token) { showOrderResult("Log in first — this endpoint requires a token."); return; }

  const form = new FormData(e.target);
  const selected = form.get("productId");
  const productId = selected === "__custom" ? form.get("customProductId") : selected;

  const items = [{
    productId,
    productName: form.get("productName"),
    quantity: Number(form.get("quantity")),
    unitPrice: Number(form.get("unitPrice")),
  }];

  const { status, data } = await placeOrder(items);
  showOrderResult({ status, ...data });
  if (status === 201 || status === 200) {
    refreshOrders();
  }
});

document.querySelectorAll(".preset").forEach((btn) => {
  btn.addEventListener("click", async () => {
    if (btn.dataset.preset === "badtoken") {
      const { status, data } = await placeOrder(
        [{ productId: "APPLE-1", productName: "Apple", quantity: 1, unitPrice: 1.5 }],
        "garbage.token.value"
      );
      showOrderResult({ status, data, note: "Sent with a deliberately mangled token — expect 401 before any handler runs." });
      return;
    }

    if (!token) { showOrderResult("Log in first — this endpoint requires a token."); return; }

    const presets = {
      happy: [{ productId: "APPLE-1", productName: "Apple", quantity: 3, unitPrice: 1.5 }],
      reject: [{ productId: "MANGO-1", productName: "Mango", quantity: 5, unitPrice: 3 }],
      poison: [{ productId: "DURIAN-1", productName: "Durian", quantity: 1, unitPrice: 8 }],
    };
    const { status, data } = await placeOrder(presets[btn.dataset.preset]);
    showOrderResult({ status, ...data });
    if (status === 201 || status === 200) refreshOrders();
  });
});

// ---- Orders list ----

function statusBadge(status) {
  return `<span class="badge ${status}">${status}</span>`;
}

async function refreshOrders() {
  if (!token) return;
  const res = await fetch(`${ORDER_API}/orders`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!res.ok) return;
  const orders = await res.json();

  if (orders.length === 0) {
    $("orders-body").innerHTML = '<tr><td colspan="5" class="empty">No orders yet — place one above.</td></tr>';
    return;
  }

  orders.sort((a, b) => new Date(b.createdAtUtc) - new Date(a.createdAtUtc));

  $("orders-body").innerHTML = orders.map((o) => `
    <tr>
      <td title="${o.id}">${o.id.slice(0, 8)}…</td>
      <td>$${Number(o.total).toFixed(2)}</td>
      <td>${statusBadge(o.status)}</td>
      <td>${new Date(o.createdAtUtc).toLocaleTimeString()}</td>
      <td><button class="secondary small" data-view="${o.id}">View</button></td>
    </tr>
  `).join("");

  document.querySelectorAll("[data-view]").forEach((btn) => {
    btn.addEventListener("click", () => viewOrder(btn.dataset.view));
  });
}

async function viewOrder(id) {
  const res = await fetch(`${ORDER_API}/orders/${id}`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  const data = await res.json();
  const detail = $("order-detail");
  detail.classList.remove("hidden");
  detail.innerHTML = `
    <strong>${data.id}</strong> — ${statusBadge(data.status)}<br/>
    ${data.rejectionReason ? `<em>${data.rejectionReason}</em><br/>` : ""}
    <ul>${data.items.map((i) => `<li>${i.productId} × ${i.quantity} @ $${i.unitPrice}</li>`).join("")}</ul>
  `;
}

$("refresh-orders").addEventListener("click", refreshOrders);

// ---- Stock ----

async function refreshStock() {
  try {
    const res = await fetch(`${STOCK_API}/stock`);
    const stock = await res.json();
    $("stock-body").innerHTML = Object.entries(stock).map(([product, qty]) => `
      <tr><td>${product}</td><td>${qty}</td></tr>
    `).join("");
  } catch {
    $("stock-body").innerHTML = '<tr><td colspan="2" class="empty">Inventory service unreachable.</td></tr>';
  }
}

// ---- Init ----

renderAuthState();
refreshStock();
refreshOrders();
setInterval(refreshStock, 3000);
setInterval(refreshOrders, 2500);
