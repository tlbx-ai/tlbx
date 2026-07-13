import { describe, expect, it, vi } from 'vitest';

import { openTerminalWebLinkInNewTab } from './webLinks';

describe('terminal web links', () => {
  it('activates recognized URLs through a temporary new-tab link', () => {
    const click = vi.fn();
    const remove = vi.fn();
    const link = {
      href: '',
      target: '',
      rel: '',
      hidden: false,
      click,
      remove,
    };
    const append = vi.fn();
    const ownerDocument = {
      createElement: vi.fn(() => link),
      body: { append },
    } as unknown as Document;
    const event = {
      preventDefault: vi.fn(),
      stopPropagation: vi.fn(),
    } as unknown as MouseEvent;

    expect(
      openTerminalWebLinkInNewTab(event, 'https://example.com/path?q=midterm', ownerDocument),
    ).toBe(true);
    expect(event.preventDefault).toHaveBeenCalledOnce();
    expect(event.stopPropagation).toHaveBeenCalledOnce();
    expect(link).toMatchObject({
      href: 'https://example.com/path?q=midterm',
      target: '_blank',
      rel: 'noopener noreferrer',
      hidden: true,
    });
    expect(append).toHaveBeenCalledWith(link);
    expect(click).toHaveBeenCalledOnce();
    expect(remove).toHaveBeenCalledOnce();
  });

  it('does not activate unsupported or malformed links', () => {
    const ownerDocument = {
      createElement: vi.fn(),
      body: { append: vi.fn() },
    } as unknown as Document;
    const event = {
      preventDefault: vi.fn(),
      stopPropagation: vi.fn(),
    } as unknown as MouseEvent;

    expect(openTerminalWebLinkInNewTab(event, 'file:///tmp/secret', ownerDocument)).toBe(false);
    expect(openTerminalWebLinkInNewTab(event, 'not a url', ownerDocument)).toBe(false);
    expect(ownerDocument.createElement).not.toHaveBeenCalled();
    expect(event.preventDefault).not.toHaveBeenCalled();
    expect(event.stopPropagation).not.toHaveBeenCalled();
  });
});
