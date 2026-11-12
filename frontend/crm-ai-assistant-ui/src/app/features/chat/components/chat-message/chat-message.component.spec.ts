import { CommonModule } from '@angular/common';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { FormsModule } from '@angular/forms';
import { ChatMessageComponent } from './chat-message.component';
import { ChatUiMessage } from '../../../../core/models/chat-response.model';

describe('ChatMessageComponent clarification Other option', () => {
  let fixture: ComponentFixture<ChatMessageComponent>;
  let component: ChatMessageComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      declarations: [ChatMessageComponent],
      imports: [CommonModule, FormsModule]
    })
      .overrideComponent(ChatMessageComponent, {
        set: {
          // Avoid pulling the CRM table child into this unit test.
          templateUrl: undefined,
          template: `
            <div *ngIf="mode === 'clarification'" class="clarification">
              <button type="button" class="other" (click)="onOtherClick()">{{ otherOptionLabel }}</button>
              <textarea *ngIf="showCustomClarification" [(ngModel)]="customClarificationText"></textarea>
              <button
                type="button"
                class="send"
                [disabled]="!canSubmitCustomClarification"
                (click)="onCustomClarificationSubmit()"
              >Send</button>
              <button
                type="button"
                class="chip"
                *ngFor="let s of message.suggestions || []"
                (click)="onSuggestionClick(s)"
              >{{ s.label }}</button>
            </div>
          `
        }
      })
      .compileComponents();

    fixture = TestBed.createComponent(ChatMessageComponent);
    component = fixture.componentInstance;
  });

  function setClarification(partial: Partial<ChatUiMessage>): void {
    const message = {
      id: '1',
      role: 'assistant',
      responseType: 'clarification',
      text: 'Which customer type should I use?',
      reply: 'Which customer type should I use?',
      suggestions: [
        {
          label: 'Account',
          question: 'Show the latest 5 sales orders with Account customer names.'
        }
      ],
      ...partial
    } as ChatUiMessage;
    fixture.componentRef.setInput('message', message);
    fixture.componentRef.setInput('suggestionsDisabled', false);
    fixture.detectChanges();
  }

  it('shows clarification mode for valid AI suggestions', () => {
    setClarification({});
    expect(component.mode).toBe('clarification');
    expect(component.otherOptionLabel).toBe('Other — Let me specify');
  });

  it('shows clarification mode when suggestions are empty', () => {
    setClarification({ suggestions: [] });
    expect(component.mode).toBe('clarification');
  });

  it('reveals custom textarea when Other is selected', () => {
    setClarification({});
    expect(component.showCustomClarification).toBeFalse();
    component.onOtherClick();
    expect(component.showCustomClarification).toBeTrue();
  });

  it('does not submit empty custom clarification', () => {
    setClarification({});
    const emitted: string[] = [];
    component.suggestionSelected.subscribe((q) => emitted.push(q));
    component.showCustomClarification = true;
    component.customClarificationText = '   ';
    component.onCustomClarificationSubmit();
    expect(emitted.length).toBe(0);
    expect(component.customSubmitting).toBeFalse();
  });

  it('submits trimmed custom clarification text', () => {
    setClarification({});
    const emitted: string[] = [];
    component.suggestionSelected.subscribe((q) => emitted.push(q));
    component.showCustomClarification = true;
    component.customClarificationText = '  Filter to English preferred language contacts.  ';
    component.onCustomClarificationSubmit();
    expect(emitted).toEqual(['Filter to English preferred language contacts.']);
    expect(component.customSubmitting).toBeTrue();
  });

  it('submits predefined suggestion question unchanged', () => {
    setClarification({});
    const emitted: string[] = [];
    component.suggestionSelected.subscribe((q) => emitted.push(q));
    component.onSuggestionClick({
      label: 'Account',
      question: 'Show the latest 5 sales orders with Account customer names.'
    });
    expect(emitted).toEqual([
      'Show the latest 5 sales orders with Account customer names.'
    ]);
  });

  it('prevents duplicate custom submit while customSubmitting', () => {
    setClarification({});
    const emitted: string[] = [];
    component.suggestionSelected.subscribe((q) => emitted.push(q));
    component.showCustomClarification = true;
    component.customClarificationText = 'Use English preferred language.';
    component.onCustomClarificationSubmit();
    component.onCustomClarificationSubmit();
    expect(emitted.length).toBe(1);
  });

  it('disables custom send when suggestionsDisabled', () => {
    setClarification({});
    component.suggestionsDisabled = true;
    component.customClarificationText = 'Use English preferred language.';
    expect(component.canSubmitCustomClarification).toBeFalse();
  });
});
