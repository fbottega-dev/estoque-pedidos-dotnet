import assert from "node:assert/strict";
const base = process.env.BASE_URL || "http://localhost:8082";
const key =
    process.env.API_KEY || "replace-with-a-random-key-at-least-16-characters";
for (let i = 0; i < 60; i++) {
    try {
        if ((await fetch(base)).ok) break;
    } catch {}
    await new Promise((r) => setTimeout(r, 1000));
}
const request = (url, body, requestKey) =>
    fetch(base + "/api" + url, {
        method: body ? "POST" : "GET",
        headers: {
            "X-Api-Key": key,
            "Content-Type": "application/json",
            ...(requestKey ? { "Idempotency-Key": requestKey } : {}),
        },
        ...(body ? { body: JSON.stringify(body) } : {}),
    });
assert.equal((await fetch(base + "/api/products")).status, 401);
const response = await request("/products", {
    sku: "LAST-" + Date.now(),
    name: "Última unidade",
    supplier: "Teste",
    minimumStock: 1,
    price: 15.5,
});
assert.equal(response.status, 201);
const p = await response.json();
assert.equal(
    (
        await request("/products/" + p.id + "/receive", {
            quantity: 1,
            reason: "Entrada de teste",
        })
    ).status,
    204,
);
const sales = await Promise.all([
    request("/orders", { productId: p.id, quantity: 1, customer: "A" }),
    request("/orders", { productId: p.id, quantity: 1, customer: "B" }),
]);
assert.deepEqual(sales.map((x) => x.status).sort(), [201, 409]);
const products = await (await request("/products")).json();
assert.equal(products.items.find((x) => x.id === p.id).stock, 0);
assert.equal((await (await request("/orders")).json()).length, 1);
assert.equal((await (await request("/movements")).json()).length, 2);
console.log("PostgreSQL concurrency + transactional HTTP smoke passed");
for (const initialStock of [1, 5]) {
    const productResponse = await request("/products", {
        sku: "RETRY-" + initialStock + "-" + Date.now(),
        name: "Pedido repetido",
        supplier: "Teste",
        minimumStock: 1,
        price: 29.9,
    });
    assert.equal(productResponse.status, 201);
    const product = await productResponse.json();
    assert.equal(
        (
            await request("/products/" + product.id + "/receive", {
                quantity: initialStock,
                reason: "Entrada",
            })
        ).status,
        204,
    );
    const requestKey = crypto.randomUUID();
    const body = {
        productId: product.id,
        quantity: 1,
        customer: "Cliente da repetição",
    };
    const attempts = await Promise.all([
        request("/orders", body, requestKey),
        request("/orders", body, requestKey),
    ]);
    assert.deepEqual(
        attempts.map((r) => r.status),
        [201, 201],
    );
    const [first, retry] = await Promise.all(attempts.map((r) => r.json()));
    assert.deepEqual(first, retry);
    const conflict = await request(
        "/orders",
        { ...body, customer: "Outro cliente" },
        requestKey,
    );
    assert.equal(conflict.status, 409);
    const inventory = await (await request("/products")).json();
    assert.equal(
        inventory.items.find((item) => item.id === product.id).stock,
        initialStock - 1,
    );
    const orders = await (await request("/orders")).json();
    assert.equal(
        orders.filter((item) => item.productId === product.id).length,
        1,
    );
    const movements = await (await request("/movements")).json();
    assert.equal(
        movements.filter((item) => item.productId === product.id).length,
        2,
    );
}
console.log(
    "PostgreSQL idempotency: simultaneous retries and conflicting payloads passed",
);
