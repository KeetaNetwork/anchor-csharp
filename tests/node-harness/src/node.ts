/*
 * Node-ledger interop harness.
 *
 * Boots the in-memory reference node with an initialized chain and mutates
 * ledger state on request (funding, account info, delegation), so a binding's
 * node client can read every surface back over the real node API.
 */

import type { ChainNode, SigningAccount } from './chain.js';
import type { HarnessResponse } from './core.js';
import { accountFromSeed } from './accounts.js';
import { bootChainNode } from './chain.js';
import { runHarness } from './core.js';

/** The running reference node, if any. */
let chain: ChainNode | undefined;

interface StartNodeRequest {
	cmd: 'startNode';
}

/** Fund the seed-derived account with `amount` base token. */
interface FundRequest {
	cmd: 'fund';
	seed: string;
	algorithm?: string;
	amount: string;
}

/** Publish on-chain account info for the seed-derived account. */
interface SetInfoRequest {
	cmd: 'setInfo';
	seed: string;
	algorithm?: string;
	name: string;
	description: string;
	metadata: string;
}

/**
 * Delegate the seed-derived account's weight to the account derived from
 * `representativeSeed`, so the binding can derive the same address and assert
 * the ledger echoes it.
 */
interface SetRepRequest {
	cmd: 'setRep';
	seed: string;
	algorithm?: string;
	representativeSeed: string;
	representativeAlgorithm?: string;
}

/** Report an account's head block hash as the reference client sees it. */
interface HeadRequest {
	cmd: 'head';
	account: string;
}

interface ShutdownRequest {
	cmd: 'shutdown';
}

type NodeRequest =
	StartNodeRequest |
	FundRequest |
	SetInfoRequest |
	SetRepRequest |
	HeadRequest |
	ShutdownRequest;

function running(): ChainNode {
	if (chain === undefined) {
		throw(new Error('no node is running; send startNode first'));
	}

	return(chain);
}

function signer(request: { seed: string; algorithm?: string }): SigningAccount {
	return(accountFromSeed(request.seed, request.algorithm));
}

async function stopNode(): Promise<void> {
	const current = chain;
	if (current === undefined) {
		return;
	}

	chain = undefined;
	await current.node.stop();
}

async function handleStartNode(): Promise<HarnessResponse> {
	await stopNode();
	chain = await bootChainNode();

	return({
		event: 'node-started',
		api: chain.api,
		baseToken: chain.repClient.baseToken.publicKeyString.get(),
		representative: chain.repClient.account.publicKeyString.get()
	});
}

async function handleFund(request: FundRequest): Promise<HarnessResponse> {
	const node = running();
	const account = signer(request);

	await node.give(account, BigInt(request.amount));

	return({ event: 'funded', account: account.publicKeyString.get() });
}

async function handleSetInfo(request: SetInfoRequest): Promise<HarnessResponse> {
	const node = running();
	const account = signer(request);
	const client = node.clientFor(account);

	await client.setInfo({
		name: request.name,
		description: request.description,
		metadata: request.metadata
	});

	return({ event: 'info-set', account: account.publicKeyString.get() });
}

async function handleSetRep(request: SetRepRequest): Promise<HarnessResponse> {
	const node = running();
	const account = signer(request);
	const client = node.clientFor(account);
	const representative = accountFromSeed(request.representativeSeed, request.representativeAlgorithm);

	const builder = client.initBuilder();
	builder.setRep(representative);

	await client.publishBuilder(builder);

	return({
		event: 'rep-set',
		account: account.publicKeyString.get(),
		representative: representative.publicKeyString.get()
	});
}

async function handleHead(request: HeadRequest): Promise<HarnessResponse> {
	const node = running();
	const block = await node.repClient.client.getHeadBlock(request.account);

	return({
		event: 'head',
		head: block === null ? null : block.hash.toString()
	});
}

async function handle(request: NodeRequest): Promise<HarnessResponse> {
	switch (request.cmd) {
		case 'startNode': return(await handleStartNode());
		case 'fund': return(await handleFund(request));
		case 'setInfo': return(await handleSetInfo(request));
		case 'setRep': return(await handleSetRep(request));
		case 'head': return(await handleHead(request));
		case 'shutdown': return({ event: 'shutdown' });
	}
}

runHarness<NodeRequest>({ event: 'ready' }, handle, stopNode);
