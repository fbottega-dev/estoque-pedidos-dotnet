# Estoque e pedidos · ASP.NET Core

[![.NET CI](https://github.com/fbottega-dev/estoque-pedidos-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/fbottega-dev/estoque-pedidos-dotnet/actions/workflows/ci.yml)

Sistema de inventário com cadastro de produtos, entradas, pedidos e histórico de movimentações. A regra central é impedir estoque negativo, inclusive quando dois compradores disputam a última unidade.

**Stack:** C# · .NET 10 · ASP.NET Core · Entity Framework Core · PostgreSQL · xUnit · Docker.

![Aplicação em execução](docs/preview.png)

## Funcionalidades

- Produtos com SKU único, fornecedor, preço e estoque mínimo.
- Entrada de mercadoria com motivo e registro de movimentação.
- Pedido de um produto por vez; preço histórico registrado na venda.
- Atualização condicional do saldo, pedido e movimentação na mesma transação.
- Filtro de reposição, paginação de produtos, últimos 50 pedidos e 100 movimentos.
- API protegida por chave de operador e limite global de 120 chamadas/minuto.

## Executar com PostgreSQL

Requisito: Docker com Compose.

```bash
cp .env.example .env
# Edite DB_PASSWORD e API_KEY (mínimo de 16 caracteres).
docker compose up --build -d
```

No PowerShell, `Copy-Item .env.example .env`. Abra **http://localhost:8082** e informe a API_KEY configurada. As migrations são aplicadas ao iniciar e o banco permanece em volume.

## Executar sem Docker — demonstração SQLite

Requisito: SDK .NET 10. No PowerShell:

```powershell
$env:API_KEY="minha-chave-local-com-16-caracteres"
$env:DatabaseProvider="Sqlite"
$env:ConnectionStrings__Stock="Data Source=stock.db"
dotnet run --project src/Estoque.Api --no-launch-profile --urls http://localhost:8082
```

No Bash:

```bash
API_KEY=minha-chave-local-com-16-caracteres DatabaseProvider=Sqlite ConnectionStrings__Stock='Data Source=stock.db' dotnet run --project src/Estoque.Api --no-launch-profile --urls http://localhost:8082
```

SQLite é uma alternativa para demonstração e testes. O schema desse modo é criado por EnsureCreated; as migrations versionadas destinam-se ao PostgreSQL. Para alterar o schema demo, use um banco novo ou implemente a migração de seus dados.

## Testar

```bash
dotnet test
```

5 testes cobrem autenticação da API, validação, SKU duplicado, saldo insuficiente, movimentação e concorrência pela última unidade em SQLite. O Actions também sobe PostgreSQL via Compose e testa a disputa pela última unidade com **duas requisições HTTP simultâneas**.

Para esse teste isolado, use um banco Compose vazio e Node.js 22: `API_KEY=<sua-chave> node scripts/smoke.mjs`. No PowerShell defina $env:API_KEY antes. O teste insere dados fictícios e espera banco vazio.

## API REST

Todas as rotas abaixo exigem o header `X-Api-Key`.

| Método | Rota | Corpo / filtro |
|---|---|---|
| GET | /api/products | ?low=true&page=0 |
| POST | /api/products | sku, name, supplier, minimumStock, price |
| POST | /api/products/{id}/receive | quantity, reason |
| POST | /api/orders | productId, quantity, customer |
| GET | /api/orders | últimos 50 pedidos |
| GET | /api/movements | últimas 100 movimentações |

```json
{"sku":"TEC-001","name":"Teclado","supplier":"Fornecedor exemplo","minimumStock":5,"price":149.90}
```

Entradas inválidas retornam 400; conflito ou saldo insuficiente, 409; chave inválida, 401. Produtos iniciam com saldo zero: registre uma entrada antes de vender.

## Por que a venda é atômica?

O UPDATE contém a condição Stock >= quantidade. Se nenhuma linha mudar, a venda falha. Caso contrário, pedido e movimento são inseridos antes do commit; uma falha reverte toda a transação. O banco também possui CHECK para impedir saldo negativo.

```mermaid
erDiagram
    PRODUCT ||--o{ ORDER : vendido_em
    PRODUCT ||--o{ MOVEMENT : possui
```

Domain contém entidades; Services reúne regras e transações; Data contém DbContext e migrations; Program define as rotas HTTP. O schema PostgreSQL pode evoluir com dotnet-ef 10.0.8 e StockDbFactory.

## Limitações e próximas entregas

- Chave única de operador, sem contas individuais ou permissões por usuário.
- Cada pedido contém um produto; carrinho com múltiplos itens é uma próxima entrega.
- Fornecedor e cliente são campos textuais, sem cadastro separado.
- Não há cancelamento, devolução, reservas nem idempotência de pedidos; reenviar um POST bem-sucedido cria outro pedido.
- Docker e servidor local destinam-se à demonstração; para acesso público, configurar HTTPS, gestão de segredos e autenticação individual.

[Uso de IA e revisão](docs/AI_USAGE.md) · [Próximas entregas](docs/ROADMAP.md)
