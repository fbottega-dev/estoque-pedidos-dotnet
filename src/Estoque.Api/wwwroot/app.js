"use strict";
const $ = (id) => document.getElementById(id);
let key = "",
  page = 0;
const money = new Intl.NumberFormat("pt-BR", {
  style: "currency",
  currency: "BRL",
});
function notice(text, error = false) {
  $("notice").textContent = text;
  $("notice").classList.toggle("error", error);
}
async function request(url, body, requestKey) {
  const response = await fetch("/api" + url, {
    method: body ? "POST" : "GET",
    headers: {
      "X-Api-Key": key,
      "Content-Type": "application/json",
      ...(requestKey ? { "Idempotency-Key": requestKey } : {}),
    },
    ...(body ? { body: JSON.stringify(body) } : {}),
  });
  if (!response.ok) {
    let detail = "";
    try {
      detail = (await response.json()).detail || "";
    } catch {}
    throw Error(
      detail ||
        (response.status === 401
          ? "Chave de acesso inválida."
          : "Não foi possível concluir a operação."),
    );
  }
  return response.status === 204 ? null : response.json();
}
function el(tag, text, cls) {
  const node = document.createElement(tag);
  node.textContent = text;
  if (cls) node.className = cls;
  return node;
}
function field(form, label, name, type = "text") {
  const id = "f-" + Math.random().toString(36).slice(2),
    input = document.createElement("input"),
    l = el("label", label);
  l.htmlFor = id;
  input.id = id;
  input.name = name;
  input.type = type;
  input.required = true;
  if (type === "number") {
    input.min = 1;
    input.max = 1000000;
    input.step = 1;
  } else input.maxLength = 120;
  form.append(l, input);
}
function formAction(label, fields, fn) {
  const form = document.createElement("form");
  for (const f of fields) field(form, ...f);
  const button = el("button", label, "secondary");
  let lastPayload = "",
    requestKey = crypto.randomUUID();
  form.append(button);
  form.onsubmit = async (event) => {
    event.preventDefault();
    button.disabled = true;
    try {
      const values = Object.fromEntries(new FormData(form));
      const payload = JSON.stringify(values);
      if (payload !== lastPayload) requestKey = crypto.randomUUID();
      lastPayload = payload;
      await fn(values, requestKey);
      form.reset();
      lastPayload = "";
      notice("Operação registrada.");
      await refresh();
    } catch (e) {
      notice(e.message, true);
    } finally {
      button.disabled = false;
    }
  };
  return form;
}
async function refresh() {
  const [products, orders, movements] = await Promise.all([
    request("/products?page=" + page + "&low=" + $("low").value),
    request("/orders"),
    request("/movements"),
  ]);
  $("products").replaceChildren();
  $("prev").disabled = page === 0;
  $("next").disabled = (page + 1) * 20 >= products.total;
  $("page").textContent =
    "Página " + (page + 1) + " · " + products.total + " produtos";
  if (!products.items.length)
    $("products").append(el("p", "Nenhum produto nesta visualização."));
  for (const p of products.items) {
    const box = el("article", "", "ticket");
    box.append(
      el("h3", p.name),
      el(
        "p",
        p.sku + " · " + money.format(p.price) + " · " + p.stock + " em estoque",
        "muted",
      ),
      el(
        "span",
        p.stock < p.minimumStock
          ? "Reposição necessária"
          : "Estoque disponível",
        "badge",
      ),
    );
    const details = document.createElement("details");
    details.append(el("summary", "Registrar entrada ou pedido"));
    details.append(
      formAction(
        "Registrar entrada",
        [
          ["Quantidade", "quantity", "number"],
          ["Motivo da entrada", "reason"],
        ],
        (data) =>
          request("/products/" + p.id + "/receive", {
            quantity: Number(data.quantity),
            reason: data.reason,
          }),
      ),
      formAction(
        "Confirmar pedido",
        [
          ["Quantidade", "quantity", "number"],
          ["Cliente", "customer"],
        ],
        (data, requestKey) =>
          request(
            "/orders",
            {
              productId: p.id,
              quantity: Number(data.quantity),
              customer: data.customer,
            },
            requestKey,
          ),
      ),
    );
    box.append(details);
    $("products").append(box);
  }
  $("orders").replaceChildren();
  if (!orders.length) $("orders").append(el("p", "Nenhum pedido registrado."));
  for (const o of orders) {
    const row = el("article", "", "ticket");
    row.append(
      el("strong", "#" + o.id + " · " + o.customer),
      el(
        "p",
        o.product +
          " · " +
          o.quantity +
          " un. · " +
          money.format(o.unitPrice * o.quantity),
      ),
    );
    $("orders").append(row);
  }
  $("movements").replaceChildren();
  if (!movements.length)
    $("movements").append(el("p", "Nenhuma movimentação registrada."));
  for (const m of movements) {
    const row = el("article", "", "ticket");
    row.append(
      el(
        "strong",
        (m.quantity > 0 ? "+" : "") + m.quantity + " · " + m.product,
      ),
      el("p", m.reason, "muted"),
    );
    $("movements").append(row);
  }
}
$("access").onsubmit = async (e) => {
  e.preventDefault();
  key = $("key").value;
  try {
    await refresh();
    $("access").hidden = true;
    $("workspace").hidden = false;
    $("access").reset();
    notice("Estoque conectado.");
  } catch (error) {
    key = "";
    notice(error.message, true);
  }
};
$("disconnect").onclick = () => {
  key = "";
  $("workspace").hidden = true;
  $("access").hidden = false;
  $("products").replaceChildren();
  $("orders").replaceChildren();
  $("movements").replaceChildren();
  notice("Acesso encerrado.");
};
$("product").onsubmit = async (e) => {
  e.preventDefault();
  const button = e.target.querySelector("button");
  button.disabled = true;
  try {
    const data = Object.fromEntries(new FormData(e.target));
    await request("/products", {
      ...data,
      minimumStock: Number(data.minimumStock),
      price: Number(data.price),
    });
    e.target.reset();
    page = 0;
    await refresh();
    notice("Produto cadastrado.");
  } catch (error) {
    notice(error.message, true);
  } finally {
    button.disabled = false;
  }
};
$("low").onchange = () => {
  page = 0;
  refresh().catch((e) => notice(e.message, true));
};
$("prev").onclick = () => {
  page--;
  refresh().catch((e) => notice(e.message, true));
};
$("next").onclick = () => {
  page++;
  refresh().catch((e) => notice(e.message, true));
};
