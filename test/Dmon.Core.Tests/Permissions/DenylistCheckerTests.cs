using Dmon.Core.Permissions;

namespace Dmon.Core.Tests.Permissions;

public sealed class DenylistCheckerTests
{
    private readonly DenylistChecker _checker = new();

    // rm against system paths
    [Theory]
    [InlineData("rm -rf /")]
    [InlineData("rm -r /etc")]
    [InlineData("rm /usr")]
    [InlineData("rm -rf /bin")]
    [InlineData("rm /var")]
    [InlineData("rm /lib")]
    [InlineData("rm /dev")]
    public void Rm_SystemPath_IsDenied(string command)
    {
        Assert.True(_checker.IsDenied(command));
    }

    [Theory]
    [InlineData("rm /var/myapp/logs")]
    [InlineData("rm -rf ./dist")]
    [InlineData("rm file.txt")]
    [InlineData("rm /home/user/document")]
    public void Rm_SafePath_IsNotDenied(string command)
    {
        Assert.False(_checker.IsDenied(command));
    }

    // mkfs
    [Theory]
    [InlineData("mkfs.ext4 /dev/sda1")]
    [InlineData("mkfs /dev/sdb")]
    [InlineData("mkfs.vfat /dev/sdc1")]
    public void Mkfs_IsDenied(string command)
    {
        Assert.True(_checker.IsDenied(command));
    }

    [Fact]
    public void MkfsUtil_IsNotDenied()
    {
        // "mkfsutil" shares a prefix with "mkfs" but is not a filesystem formatter.
        Assert.False(_checker.IsDenied("mkfsutil /dev/sda1"));
    }

    // dd if=/dev/zero
    [Theory]
    [InlineData("dd if=/dev/zero of=/dev/sda")]
    [InlineData("dd if=/dev/zero of=file bs=1M count=100")]
    public void DdZero_IsDenied(string command)
    {
        Assert.True(_checker.IsDenied(command));
    }

    [Fact]
    public void DdNonZero_IsNotDenied()
    {
        Assert.False(_checker.IsDenied("dd if=input.img of=output.img"));
    }

    // shred
    [Theory]
    [InlineData("shred /dev/sda")]
    [InlineData("shred -v file.txt")]
    [InlineData("shred")]
    public void Shred_IsDenied(string command)
    {
        Assert.True(_checker.IsDenied(command));
    }

    // chmod 777 system path
    [Theory]
    [InlineData("chmod -R 777 /")]
    [InlineData("chmod 777 /etc")]
    [InlineData("chmod -R 777 /usr")]
    public void Chmod777SystemPath_IsDenied(string command)
    {
        Assert.True(_checker.IsDenied(command));
    }

    [Theory]
    [InlineData("chmod 755 /home/user/script.sh")]
    [InlineData("chmod 644 file.txt")]
    public void Chmod_SafePath_IsNotDenied(string command)
    {
        Assert.False(_checker.IsDenied(command));
    }

    // chattr system path
    [Theory]
    [InlineData("chattr -i /etc/passwd")]
    [InlineData("chattr +i /boot/grub")]
    public void Chattr_SystemPath_IsDenied(string command)
    {
        Assert.True(_checker.IsDenied(command));
    }

    [Fact]
    public void Chattr_SafePath_IsNotDenied()
    {
        Assert.False(_checker.IsDenied("chattr +i /home/user/important.txt"));
    }

    // Fork bombs — canonical :(){ :|:& };: pattern and variants containing :|: or :()
    [Theory]
    [InlineData(":(){ :|:& };:")]
    [InlineData(":(){ :(); }")]
    public void ForkBomb_IsDenied(string command)
    {
        Assert.True(_checker.IsDenied(command));
    }

    // sudo
    [Theory]
    [InlineData("sudo rm -rf /")]
    [InlineData("sudo apt install vim")]
    [InlineData("sudo")]
    public void Sudo_IsDenied(string command)
    {
        Assert.True(_checker.IsDenied(command));
    }

    // su — must match su and su <args> but NOT sudo, subversion, sublime
    [Theory]
    [InlineData("su")]
    [InlineData("su root")]
    [InlineData("su -c 'whoami'")]
    public void Su_IsDenied(string command)
    {
        Assert.True(_checker.IsDenied(command));
    }

    [Theory]
    [InlineData("subversion update")]
    [InlineData("sublime .")]
    [InlineData("svn update")]
    public void SuPrefix_IsNotDenied(string command)
    {
        Assert.False(_checker.IsDenied(command));
    }

    // Safe commands should not be denied
    [Theory]
    [InlineData("git commit -m \"message\"")]
    [InlineData("dotnet build")]
    [InlineData("ls -la")]
    [InlineData("npm install")]
    public void SafeCommands_AreNotDenied(string command)
    {
        Assert.False(_checker.IsDenied(command));
    }
}
