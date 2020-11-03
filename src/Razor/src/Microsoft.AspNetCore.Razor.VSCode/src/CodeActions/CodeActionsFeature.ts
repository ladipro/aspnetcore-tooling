/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Microsoft Corporation. All rights reserved.
 *  Licensed under the MIT License. See License.txt in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import * as vscode from 'vscode';
import { ClientCapabilities, DocumentSelector, InitializeParams, RequestType, ServerCapabilities, StaticFeature } from 'vscode-languageclient';
import { RazorDocumentManager } from '../RazorDocumentManager';
import { RazorLanguageServerClient } from '../RazorLanguageServerClient';
import { RazorLogger } from '../RazorLogger';
import { RazorCodeAction } from '../RPC/RazorCodeAction';
import { SerializableCodeActionParams } from '../RPC/SerializableCodeActionParams';
import { convertRangeFromSerializable } from '../RPC/SerializableRange';

export class CodeActionsFeature implements StaticFeature {

    private static readonly provideCodeActionsEndpoint = 'razor/provideCodeActions';
    public fillInitializeParams?: ((params: InitializeParams) => void) | undefined;
    private codeActionRequestType: RequestType<SerializableCodeActionParams, RazorCodeAction[], any, any> = new RequestType(CodeActionsFeature.provideCodeActionsEndpoint);
    private emptyCodeActionResponse: RazorCodeAction[] = [];

    constructor(
        private readonly documentManager: RazorDocumentManager,
        private readonly serverClient: RazorLanguageServerClient,
        private readonly logger: RazorLogger) {
    }
    public fillClientCapabilities(capabilities: ClientCapabilities): void {
        return;
    }

    public async initialize(capabilities: ServerCapabilities, documentSelector: DocumentSelector | undefined): Promise<void> {
        await this.serverClient.onRequestWithParams<SerializableCodeActionParams, RazorCodeAction[], any, any>(
            this.codeActionRequestType,
            async (request, token) => this.provideCodeActions(request, token));
    }

    private async provideCodeActions(
        codeActionParams: SerializableCodeActionParams,
        cancellationToken: vscode.CancellationToken) {
        try {
            const razorDocumentUri = vscode.Uri.parse(codeActionParams.textDocument.uri);
            const razorDocument = await this.documentManager.getDocument(razorDocumentUri);
            if (razorDocument === undefined) {
                return this.emptyCodeActionResponse;
            }

            const virtualCSharpUri = razorDocument.csharpDocument.uri;

            const range = convertRangeFromSerializable(codeActionParams.range);

            const commands = await vscode.commands.executeCommand<vscode.Command[]>(
                'vscode.executeCodeActionProvider',
                virtualCSharpUri,
                range) as vscode.Command[];

            if (commands.length === 0) {
                return this.emptyCodeActionResponse;
            }

            return commands.map(c => this.commandAsCodeAction(c));
        } catch (error) {
            this.logger.logWarning(`${CodeActionsFeature.provideCodeActionsEndpoint} failed with ${error}`);
        }

        return this.emptyCodeActionResponse;
    }

    private commandAsCodeAction(command: vscode.Command): RazorCodeAction {
        return { title: command.title } as RazorCodeAction;
    }
}