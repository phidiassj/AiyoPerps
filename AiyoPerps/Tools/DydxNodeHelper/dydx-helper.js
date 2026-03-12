const Long = require('long');
const {
  CompositeClient,
  LocalWallet,
  Network,
  OrderExecution,
  OrderSide,
  OrderTimeInForce,
  OrderType,
  SelectedGasDenom,
  SubaccountInfo,
  tradingKeyUtils,
} = require('@dydxprotocol/v4-client-js');

function respond(payload) {
  process.stdout.write(`${JSON.stringify(payload)}\n`);
}

function fail(message, extra) {
  respond({
    success: false,
    error: message,
    ...extra,
  });
}

function stripHexPrefix(value) {
  return typeof value === 'string' && value.startsWith('0x')
    ? value.slice(2)
    : value;
}

async function readInput() {
  const chunks = [];
  for await (const chunk of process.stdin) {
    chunks.push(chunk);
  }

  const text = Buffer.concat(chunks).toString('utf8').trim();
  return text.length === 0 ? {} : JSON.parse(text);
}

function getNetwork(environment) {
  return String(environment).toLowerCase() === 'mainnet'
    ? Network.mainnet()
    : Network.testnet();
}

function getSubaccountNumber(input) {
  const parsed = Number.parseInt(`${input.subaccountNumber ?? 0}`, 10);
  return Number.isFinite(parsed) && parsed >= 0
    ? parsed
    : 0;
}

async function connectClient(environment) {
  const client = await CompositeClient.connect(getNetwork(environment));
  client.setSelectedGasDenom(SelectedGasDenom.NATIVE);
  return client;
}

async function buildTradingContext(client, input) {
  const accountAddress = `${input.accountAddress ?? ''}`.trim();
  const wallet = await LocalWallet.fromPrivateKey(stripHexPrefix(`${input.privateKey ?? ''}`), 'dydx');
  const derivedWalletAddress = `${wallet.address ?? ''}`.trim();
  const configuredWalletAddress = `${input.walletAddress ?? ''}`.trim();
  const subaccountNumber = getSubaccountNumber(input);

  if (!accountAddress) {
    throw new Error('accountAddress is required');
  }

  if (!derivedWalletAddress) {
    throw new Error('Unable to derive dYdX wallet address from privateKey');
  }

  if (derivedWalletAddress.toLowerCase() === accountAddress.toLowerCase()) {
    return {
      accountAddress,
      derivedWalletAddress,
      authenticatorIds: [],
      permissioned: false,
      subaccount: SubaccountInfo.forLocalWallet(wallet, subaccountNumber),
    };
  }

  const auths = await client.getAuthenticators(accountAddress);
  const metadata = tradingKeyUtils.getAuthorizedTradingKeysMetadata(auths.accountAuthenticators);
  const authenticatorIds = metadata
    .filter((item) => item.address.toLowerCase() === derivedWalletAddress.toLowerCase())
    .map((item) => Long.fromString(item.id));

  if (authenticatorIds.length === 0) {
    if (configuredWalletAddress &&
        configuredWalletAddress.toLowerCase() !== derivedWalletAddress.toLowerCase()) {
      throw new Error(
        `WalletAddress does not match the configured PrivateKey. configured=${configuredWalletAddress}, derived=${derivedWalletAddress}`,
      );
    }

    throw new Error(
      `No matching dYdX trading authenticator found for wallet ${derivedWalletAddress}. ` +
      'Create an API Trading Key in dYdX first, or use the owner wallet directly.',
    );
  }

  return {
    accountAddress,
    derivedWalletAddress,
    authenticatorIds,
    permissioned: true,
    subaccount: SubaccountInfo.forPermissionedWallet(
      wallet,
      accountAddress,
      subaccountNumber,
      authenticatorIds,
    ),
  };
}

async function getMarket(client, marketId) {
  const response = await client.indexerClient.markets.getPerpetualMarkets(marketId);
  const market = response?.markets?.[marketId];
  if (market == null) {
    throw new Error(`Market not found: ${marketId}`);
  }

  return market;
}

function firstPositiveLevel(levels) {
  if (!Array.isArray(levels)) {
    return 0;
  }

  for (const level of levels) {
    const price = Number.parseFloat(`${level?.price ?? level?.[0] ?? 0}`);
    if (Number.isFinite(price) && price > 0) {
      return price;
    }
  }

  return 0;
}

async function resolveAggressiveMarketPrice(client, marketId, side, fallbackPrice) {
  const fallback = Number.isFinite(fallbackPrice) && fallbackPrice > 0
    ? fallbackPrice
    : 1;

  try {
    const orderbook = await client.indexerClient.markets.getPerpetualMarketOrderbook(marketId);
    const bestBid = firstPositiveLevel(orderbook?.bids);
    const bestAsk = firstPositiveLevel(orderbook?.asks);
    if (side === OrderSide.BUY) {
      const anchor = bestAsk > 0 ? bestAsk : fallback;
      return anchor * 1.05;
    }

    const anchor = bestBid > 0 ? bestBid : fallback;
    return Math.max(anchor * 0.95, 0.00000001);
  } catch {
    if (side === OrderSide.BUY) {
      return fallback * 1.05;
    }

    return Math.max(fallback * 0.95, 0.00000001);
  }
}

function normalizeHash(tx) {
  const hash = tx?.hash;
  if (hash == null) {
    return '';
  }

  if (typeof hash === 'string') {
    return hash;
  }

  if (Array.isArray(hash)) {
    return Buffer.from(hash).toString('hex');
  }

  if (hash.type === 'Buffer' && Array.isArray(hash.data)) {
    return Buffer.from(hash.data).toString('hex');
  }

  return `${hash}`;
}

async function handleValidate(input) {
  const client = await connectClient(input.environment);
  const ctx = await buildTradingContext(client, input);
  respond({
    success: true,
    accountAddress: ctx.accountAddress,
    walletAddress: ctx.derivedWalletAddress,
    subaccountNumber: ctx.subaccount.subaccountNumber,
    permissioned: ctx.permissioned,
    authenticatorIds: ctx.authenticatorIds.map((item) => item.toString()),
  });
}

async function handlePlace(input) {
  const client = await connectClient(input.environment);
  const ctx = await buildTradingContext(client, input);
  const marketId = `${input.marketId ?? ''}`.trim();
  if (!marketId) {
    throw new Error('marketId is required');
  }

  const market = await getMarket(client, marketId);
  const size = Number.parseFloat(`${input.size ?? 0}`);
  if (!Number.isFinite(size) || size <= 0) {
    throw new Error('size must be positive');
  }

  const clientId = Number.isFinite(Number(input.clientId))
    ? Number(input.clientId)
    : Math.floor(Math.random() * 0xffffffff);
  const reduceOnly = Boolean(input.reduceOnly);
  const side = `${input.side ?? ''}`.trim().toLowerCase() === 'sell'
    ? OrderSide.SELL
    : OrderSide.BUY;
  const rawOrderType = `${input.orderType ?? ''}`.trim().toLowerCase();
  const executionText = `${input.execution ?? ''}`.trim().toUpperCase();
  const execution = executionText === 'POST_ONLY'
    ? OrderExecution.POST_ONLY
    : executionText === 'FOK'
      ? OrderExecution.FOK
      : executionText === 'IOC'
        ? OrderExecution.IOC
        : OrderExecution.DEFAULT;

  let tx;
  if (rawOrderType === 'limit' || rawOrderType === 'stop_limit' || rawOrderType === 'take_profit_limit') {
    const price = Number.parseFloat(`${input.price ?? 0}`);
    if (!Number.isFinite(price) || price <= 0) {
      throw new Error('price must be positive for limit orders');
    }

    const triggerPrice = rawOrderType === 'stop_limit' || rawOrderType === 'take_profit_limit'
      ? Number.parseFloat(`${input.triggerPrice ?? 0}`)
      : undefined;
    if ((rawOrderType === 'stop_limit' || rawOrderType === 'take_profit_limit') &&
        (!Number.isFinite(triggerPrice) || triggerPrice <= 0)) {
      throw new Error('triggerPrice must be positive for conditional limit orders');
    }

    const reduceOnlyTif = reduceOnly && rawOrderType === 'limit'
      ? OrderTimeInForce.IOC
      : OrderTimeInForce.GTT;
    const reduceOnlyExecution = reduceOnly && rawOrderType === 'limit'
      ? OrderExecution.IOC
      : execution;
    const goodTilTimeInSeconds = reduceOnly && rawOrderType === 'limit'
      ? 0
      : Math.max(
          300,
          Number.parseInt(`${input.goodTilTimeInSeconds ?? 604800}`, 10) || 604800,
        );
    const type = rawOrderType === 'stop_limit'
      ? OrderType.STOP_LIMIT
      : rawOrderType === 'take_profit_limit'
        ? OrderType.TAKE_PROFIT_LIMIT
        : OrderType.LIMIT;

    tx = await client.placeOrder(
      ctx.subaccount,
      marketId,
      type,
      side,
      price,
      size,
      clientId,
      reduceOnlyTif,
      goodTilTimeInSeconds,
      reduceOnlyExecution,
      false,
      reduceOnly,
      triggerPrice,
    );

    respond({
      success: true,
      txHash: normalizeHash(tx),
      clientId,
      orderType: rawOrderType,
      triggerPrice,
      goodTilTimeInSeconds,
      marketType: `${market.marketType ?? ''}`,
      permissioned: ctx.permissioned,
    });
    return;
  }

  const fallbackPrice = Number.parseFloat(`${market.oraclePrice ?? market.indexPrice ?? 0}`);
  const marketPrice = await resolveAggressiveMarketPrice(client, marketId, side, fallbackPrice);
  tx = await client.placeOrder(
    ctx.subaccount,
    marketId,
    OrderType.MARKET,
    side,
    marketPrice,
    size,
    clientId,
    OrderTimeInForce.IOC,
    0,
    OrderExecution.IOC,
    false,
    reduceOnly,
  );

  respond({
    success: true,
    txHash: normalizeHash(tx),
    clientId,
    marketType: `${market.marketType ?? ''}`,
    permissioned: ctx.permissioned,
  });
}

async function handleCancel(input) {
  const client = await connectClient(input.environment);
  const ctx = await buildTradingContext(client, input);
  const clientId = Number.parseInt(`${input.clientId ?? 0}`, 10);
  const clobPairId = Number.parseInt(`${input.clobPairId ?? 0}`, 10);
  const orderFlags = Number.parseInt(`${input.orderFlags ?? 0}`, 10);
  const goodTilBlockRaw = Number.parseInt(`${input.goodTilBlock ?? 0}`, 10);
  const goodTilBlockTimeRaw = Number.parseInt(`${input.goodTilBlockTime ?? 0}`, 10);

  if (!Number.isFinite(clientId) || clientId <= 0) {
    throw new Error('clientId is required');
  }

  if (!Number.isFinite(clobPairId) || clobPairId < 0) {
    throw new Error('clobPairId is required');
  }

  if (!Number.isFinite(orderFlags) || orderFlags < 0) {
    throw new Error('orderFlags is required');
  }

  const tx = await client.cancelRawOrder(
    ctx.subaccount,
    clientId,
    orderFlags,
    clobPairId,
    goodTilBlockRaw > 0 ? goodTilBlockRaw : undefined,
    goodTilBlockTimeRaw > 0 ? goodTilBlockTimeRaw : undefined,
  );

  respond({
    success: true,
    txHash: normalizeHash(tx),
    permissioned: ctx.permissioned,
  });
}

async function handleTransfer(input) {
  const client = await connectClient(input.environment);
  const ctx = await buildTradingContext(client, input);
  const recipientSubaccountNumber = Number.parseInt(`${input.recipientSubaccountNumber ?? 0}`, 10);
  const amount = `${input.amount ?? ''}`.trim();

  if (ctx.permissioned) {
    throw new Error(
      'dYdX isolated transfers require the owner wallet. API Trading Keys are limited to cross subaccount order actions.',
    );
  }

  if (!Number.isFinite(recipientSubaccountNumber) || recipientSubaccountNumber < 0) {
    throw new Error('recipientSubaccountNumber must be a non-negative integer');
  }

  const amountValue = Number.parseFloat(amount);
  if (!Number.isFinite(amountValue) || amountValue <= 0) {
    throw new Error('amount must be positive');
  }

  const tx = await client.transferToSubaccount(
    ctx.subaccount,
    ctx.accountAddress,
    recipientSubaccountNumber,
    amount,
  );

  respond({
    success: true,
    txHash: normalizeHash(tx),
    sourceSubaccountNumber: ctx.subaccount.subaccountNumber,
    recipientSubaccountNumber,
    amount,
    permissioned: ctx.permissioned,
  });
}

async function main() {
  const command = `${process.argv[2] ?? ''}`.trim().toLowerCase();
  const input = await readInput();

  switch (command) {
    case 'validate':
      await handleValidate(input);
      return;
    case 'place':
      await handlePlace(input);
      return;
    case 'cancel':
      await handleCancel(input);
      return;
    case 'transfer':
      await handleTransfer(input);
      return;
    default:
      fail(`Unsupported command: ${command || '(empty)'}`);
      return;
  }
}

main().catch((error) => {
  fail(error?.message ?? `${error}`);
});
