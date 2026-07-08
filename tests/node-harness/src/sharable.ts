/*
 * SharableCertificateAttributes interop harness.
 */

import * as http from 'node:http';

import type * as CertificatesModule from '@keetanetwork/anchor/lib/certificates.js';
import type * as KeetaNetModule from '@keetanetwork/keetanet-client';

import type { HarnessResponse } from './core.js';
import { accountFromSeed } from './accounts.js';
import { referenceResolver, runHarness } from './core.js';
import { reviveValue } from './values.js';

const refs = referenceResolver();
const certificates = await refs.anchor<typeof CertificatesModule>('lib/certificates.js');
const KeetaNet = refs.client<typeof KeetaNetModule>();

const Account = KeetaNet.lib.Account;
const SharableCertificateAttributes = certificates.SharableCertificateAttributes;

type BaseCertificate = InstanceType<typeof KeetaNet.lib.Utils.Certificate.Certificate>;

/**
 * A single attribute to embed in the issued leaf: its friendly `name`, whether
 * it is `sensitive` (encrypted and proven) or plain, and its `value`.
 */
interface SharableAttribute {
	name: string;
	sensitive: boolean;
	value: unknown;
}

/**
 * Build a leaf for `subjectSeed`, wrap the named attributes in a sharable
 * bundle, and grant `recipientSeed` access. `algorithm` names the curve both
 * sides derive the subject and recipient accounts on.
 */
interface BuildSharableRequest {
	cmd: 'buildSharable';
	subjectSeed: string;
	recipientSeed: string;
	attributes: SharableAttribute[];
	algorithm?: string;
}

/**
 * Open a sharable bundle the C# binding exported, reading each named attribute
 * back through the reference so both sides can compare buffers.
 */
interface ReadSharableRequest {
	cmd: 'readSharable';
	pem: string;
	recipientSeed: string;
	names: string[];
	algorithm?: string;
}

/**
 * Serve a stored blob over HTTP for reference-fetch tests. `data` is the
 * base64 stored bytes; `wrap` serves them inside the storage-service
 * `{ data, mimeType }` JSON convention instead of raw.
 */
interface ServeBlobRequest {
	cmd: 'serveBlob';
	data: string;
	mimeType: string;
	wrap?: boolean;
}

/**
 * Open a sharable bundle PEM with the reference reader, decode the named
 * attributes, and resolve every `$blob` reference on them to its verified
 * bytes (the reference reader throws on an access-time digest mismatch).
 */
interface OpenSharableRequest {
	cmd: 'openSharable';
	pem: string;
	recipientSeed: string;
	attributes: string[];
	algorithm?: string;
}

interface ShutdownRequest {
	cmd: 'shutdown';
}

type SharableRequest =
	BuildSharableRequest |
	ReadSharableRequest |
	ServeBlobRequest |
	OpenSharableRequest |
	ShutdownRequest;

/**
 * Read each named attribute buffer back through a populated bundle, encoding it
 * as base64 so the C# side can compare byte-for-byte.
 */
async function readBuffers(
	sharable: InstanceType<typeof SharableCertificateAttributes>,
	names: string[]
): Promise<{ [name: string]: string | null }> {
	const buffers: { [name: string]: string | null } = {};
	for (const name of names) {
		const buffer = await sharable.getAttributeBuffer(name);
		if (buffer === undefined) {
			buffers[name] = null;
		} else {
			buffers[name] = Buffer.from(buffer).toString('base64');
		}
	}

	return(buffers);
}

/**
 * Issue a leaf carrying the requested attributes, wrap the named ones in a
 * sharable bundle for the recipient, and return the exported PEM alongside the
 * reference's own view of each disclosed buffer.
 */
async function handleBuildSharable(request: BuildSharableRequest): Promise<HarnessResponse> {
	const subjectAccount = accountFromSeed(request.subjectSeed, request.algorithm);
	const publicKeyString = subjectAccount.publicKeyString.get();
	const subjectNoPrivate = Account.fromPublicKeyString(publicKeyString);
	const seed = Account.generateRandomSeed();
	const issuer = Account.fromSeed(seed, 0);

	const builder = new certificates.CertificateBuilder({
		issuer,
		subject: subjectNoPrivate,
		validFrom: new Date(Date.now() - 30_000),
		validTo: new Date(Date.now() + (60 * 60 * 1000))
	});

	for (const attribute of request.attributes) {
		// eslint-disable-next-line @typescript-eslint/consistent-type-assertions
		const name = attribute.name as CertificatesModule.CertificateAttributeNames;
		// eslint-disable-next-line @typescript-eslint/consistent-type-assertions
		builder.setAttribute(name, attribute.sensitive, reviveValue(attribute.value) as never);
	}

	const leaf = await builder.build({ serial: 4 });
	const reader = new certificates.Certificate(leaf, { subjectKey: subjectAccount, moment: null });

	const names = request.attributes.map(function(attribute) {
		return(attribute.name);
	});
	// eslint-disable-next-line @typescript-eslint/consistent-type-assertions
	const attributeNames = names as CertificatesModule.CertificateAttributeNames[];
	const intermediates = new Set<BaseCertificate>();
	const sharable = await SharableCertificateAttributes.fromCertificate(reader, intermediates, attributeNames);

	const recipient = accountFromSeed(request.recipientSeed, request.algorithm);
	await sharable.grantAccess(recipient);

	const pem = await sharable.export({ format: 'string' });
	const buffers = await readBuffers(sharable, names);

	return({ event: 'sharable-built', pem, buffers });
}

/**
 * Open a C#-exported bundle with the recipient key and read the named
 * attribute buffers back through the reference.
 */
async function handleReadSharable(request: ReadSharableRequest): Promise<HarnessResponse> {
	const recipient = accountFromSeed(request.recipientSeed, request.algorithm);
	const sharable = new SharableCertificateAttributes(request.pem, { principals: recipient });

	const buffers = await readBuffers(sharable, request.names);
	return({ event: 'sharable-read', buffers });
}

/**
 * A single-process HTTP blob store for reference-fetch tests: each served blob
 * gets a unique path on one listener.
 */
let blobServer: {
	server: http.Server;
	url: string;
	blobs: Map<string, { body: Buffer; contentType: string }>;
} | undefined;

/**
 * Start the blob listener on a random loopback port on first use and reuse it
 * for every later blob.
 */
async function ensureBlobServer(): Promise<NonNullable<typeof blobServer>> {
	const current = blobServer;
	if (current !== undefined) {
		return(current);
	}

	const blobs = new Map<string, { body: Buffer; contentType: string }>();
	const server = http.createServer(function(request, response) {
		const key = (request.url ?? '').replace(/^\//, '');
		const entry = blobs.get(key);
		if (entry === undefined) {
			response.writeHead(404);
			response.end();
			return;
		}

		response.writeHead(200, { 'content-type': entry.contentType });
		response.end(entry.body);
	});

	await new Promise<void>(function(resolve) {
		server.listen(0, '127.0.0.1', resolve);
	});

	const address = server.address();
	if (address === null || typeof address === 'string') {
		throw(new Error('blob server did not report a bound port'));
	}

	const started = { server, url: `http://127.0.0.1:${address.port}/`, blobs };
	blobServer = started;
	return(started);
}

/**
 * Close the blob listener if one is running, so the harness process can exit.
 */
async function stopBlobServer(): Promise<void> {
	const current = blobServer;
	if (current === undefined) {
		return;
	}

	blobServer = undefined;
	await new Promise<void>(function(resolve) {
		current.server.close(() => { resolve(); });
	});
}

/**
 * Serve the request's bytes over HTTP and return the URL. With `wrap`, the
 * bytes are served in the storage-service `{data, mimeType}` JSON convention
 * instead of raw.
 */
async function handleServeBlob(request: ServeBlobRequest): Promise<HarnessResponse> {
	const store = await ensureBlobServer();
	const raw = Buffer.from(request.data, 'base64');

	let body = raw;
	let contentType = request.mimeType;
	if (request.wrap === true) {
		body = Buffer.from(JSON.stringify({ data: raw.toString('base64'), mimeType: request.mimeType }));
		contentType = 'application/json';
	}

	const key = `blob-${store.blobs.size}`;
	store.blobs.set(key, { body, contentType });
	return({ event: 'blob-served', url: `${store.url}${key}` });
}

/**
 * Resolve every `$blob` closure in a decoded attribute `value`.
 */
async function resolveBlobReferences(
	value: unknown,
	resolved: { [id: string]: { data: string; type: string }}
): Promise<void> {
	const pending: unknown[] = [value];
	while (pending.length > 0) {
		const node = pending.pop();
		if (Array.isArray(node)) {
			const items: unknown[] = node;
			pending.push(...items);
			continue;
		}
		if (node === null || typeof node !== 'object' || Buffer.isBuffer(node)) {
			continue;
		}

		// eslint-disable-next-line @typescript-eslint/consistent-type-assertions
		const record = node as { [key: string]: unknown };
		const blobFunction = record['$blob'];
		if (typeof blobFunction !== 'function') {
			pending.push(...Object.values(record));
			continue;
		}

		const digestInfo = record['digest'];
		if (digestInfo === null || typeof digestInfo !== 'object' || !('digest' in digestInfo) || !Buffer.isBuffer(digestInfo.digest)) {
			throw(new Error('$blob node does not carry a Buffer digest'));
		}

		const id = digestInfo.digest.toString('hex').toUpperCase();
		// eslint-disable-next-line @typescript-eslint/consistent-type-assertions
		const resolve = blobFunction as () => Promise<Blob>;
		const blob = await resolve();
		const bytes = Buffer.from(await blob.arrayBuffer());

		resolved[id] = { data: bytes.toString('base64'), type: blob.type };

		delete record['$blob'];
	}
}

/**
 * Open a sharable bundle PEM with the reference `SharableCertificateAttributes`
 * reader and return the decoded attribute values alongside every resolved,
 * digest-verified `$blob` payload, keyed by attribute name then reference id.
 */
async function handleOpenSharable(request: OpenSharableRequest): Promise<HarnessResponse> {
	const recipient = accountFromSeed(request.recipientSeed, request.algorithm);
	const sharable = new SharableCertificateAttributes(request.pem, { principals: recipient });

	const attributes: { [name: string]: unknown } = {};
	const blobs: { [name: string]: { [id: string]: { data: string; type: string }}} = {};
	for (const name of request.attributes) {
		// eslint-disable-next-line @typescript-eslint/consistent-type-assertions
		const attributeName = name as CertificatesModule.CertificateAttributeNames;
		const value = await sharable.getAttribute(attributeName);
		const resolved: { [id: string]: { data: string; type: string }} = {};

		await resolveBlobReferences(value, resolved);

		attributes[name] = value;
		blobs[name] = resolved;
	}

	return({ event: 'sharable-opened', attributes, blobs });
}

async function handle(request: SharableRequest): Promise<HarnessResponse> {
	switch (request.cmd) {
		case 'buildSharable': return(await handleBuildSharable(request));
		case 'readSharable': return(await handleReadSharable(request));
		case 'serveBlob': return(await handleServeBlob(request));
		case 'openSharable': return(await handleOpenSharable(request));
		case 'shutdown': return({ event: 'shutdown' });
	}
}

runHarness<SharableRequest>({ event: 'ready' }, handle, stopBlobServer);
