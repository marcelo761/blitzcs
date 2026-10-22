; All Includes use this main file's directory as their base.
Include "state.bb"
Include "logic.bb"
Include "bridge.bb"
answer=CS::ProjectBridge.Compute()
<?cs BB.answer += 2; ?>
Print answer
